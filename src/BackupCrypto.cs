using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace LoveMyShelf {
    // The lock on a backup file. The password becomes a key with Argon2id (RFC 9106: it needs a lot of memory,
    // so guessing passwords is slow even on many graphics cards), and the key locks the file in pieces with
    // AES-256-GCM from Windows itself (CNG, the same code Windows uses; it also tells when a piece was changed).
    // Nothing here leaves the computer and the password is never written anywhere.
    internal static class BackupCrypto {
        // ---------------- Argon2id (version 0x13) ----------------
        internal static byte[] Argon2id(byte[] password,byte[] salt,int memoryKiB,int passes,int lanes,int length,byte[] secret=null,byte[] extra=null) {
            if(lanes<1 || passes<1 || memoryKiB<8*lanes || length<4)throw new ArgumentException("Argon2 settings");
            secret=secret??new byte[0];extra=extra??new byte[0];
            var h=new Blake2b(64);
            h.Update(Le32(lanes));h.Update(Le32(length));h.Update(Le32(memoryKiB));h.Update(Le32(passes));
            h.Update(Le32(0x13));h.Update(Le32(2));
            h.Update(Le32(password.Length));h.Update(password);
            h.Update(Le32(salt.Length));h.Update(salt);
            h.Update(Le32(secret.Length));h.Update(secret);
            h.Update(Le32(extra.Length));h.Update(extra);
            byte[] h0=h.Final();
            int segment=memoryKiB/(4*lanes);
            int laneLength=segment*4,blocks=laneLength*lanes;
            var memory=new ulong[blocks][];
            for(int i=0;i<blocks;i++)memory[i]=new ulong[128];
            var seed=new byte[72];Buffer.BlockCopy(h0,0,seed,0,64);
            for(int l=0;l<lanes;l++) {
                for(int k=0;k<2;k++) {
                    Buffer.BlockCopy(Le32(k),0,seed,64,4);Buffer.BlockCopy(Le32(l),0,seed,68,4);
                    ToWords(LongHash(1024,seed),memory[l*laneLength+k]);
                }
            }
            for(int pass=0;pass<passes;pass++)
                for(int slice=0;slice<4;slice++) {
                    int p=pass,s=slice;
                    Parallel.For(0,lanes,l=>FillSegment(memory,p,l,s,lanes,laneLength,segment,blocks,passes));
                }
            var last=new ulong[128];
            for(int l=0;l<lanes;l++) {var b=memory[l*laneLength+laneLength-1];for(int i=0;i<128;i++)last[i]^=b[i];}
            Array.Clear(seed,0,seed.Length);
            return LongHash(length,FromWords(last));
        }
        static void FillSegment(ulong[][] memory,int pass,int lane,int slice,int lanes,int laneLength,int segment,int blocks,int passes) {
            bool independent=pass==0 && slice<2;
            ulong[] address=null,input=null,zero=null;
            var tmp=new ulong[128];
            int start=0;
            if(independent) {
                address=new ulong[128];input=new ulong[128];zero=new ulong[128];
                input[0]=(ulong)pass;input[1]=(ulong)lane;input[2]=(ulong)slice;input[3]=(ulong)blocks;input[4]=(ulong)passes;input[5]=2;
            }
            if(pass==0 && slice==0) {
                start=2;
                if(independent)NextAddresses(address,input,zero,tmp);
            }
            int current=lane*laneLength+slice*segment+start;
            int previous=current%laneLength==0?current+laneLength-1:current-1;
            for(int i=start;i<segment;i++,current++,previous++) {
                if(current%laneLength==1)previous=current-1;
                ulong random;
                if(independent) {
                    if(i%128==0)NextAddresses(address,input,zero,tmp);
                    random=address[i%128];
                } else random=memory[previous][0];
                int refLane=(int)((random>>32)%(ulong)lanes);
                if(pass==0 && slice==0)refLane=lane;
                int refIndex=IndexAlpha(pass,slice,i,(uint)random,refLane==lane,laneLength,segment);
                Fill(memory[previous],memory[refLane*laneLength+refIndex],memory[current],pass!=0,tmp);
            }
        }
        static int IndexAlpha(int pass,int slice,int index,uint random,bool sameLane,int laneLength,int segment) {
            long area;
            if(pass==0) {
                if(slice==0)area=index-1;
                else if(sameLane)area=(long)slice*segment+index-1;
                else area=(long)slice*segment+(index==0?-1:0);
            } else {
                if(sameLane)area=laneLength-segment+index-1;
                else area=laneLength-segment+(index==0?-1:0);
            }
            ulong x=(ulong)random*random>>32;
            ulong relative=(ulong)area-1-((ulong)area*x>>32);
            long startAt=pass!=0?(slice==3?0:(slice+1)*segment):0;
            return (int)(((ulong)startAt+relative)%(ulong)laneLength);
        }
        static void NextAddresses(ulong[] address,ulong[] input,ulong[] zero,ulong[] tmp) {
            input[6]++;
            Fill(zero,input,address,false,tmp);
            Fill(zero,address,address,false,tmp);
        }
        // G of RFC 9106 section 3.5: next = P(previous XOR reference) XOR previous XOR reference (XOR next in later passes).
        static void Fill(ulong[] previous,ulong[] reference,ulong[] next,bool withXor,ulong[] r) {
            var keep=new ulong[128];
            for(int i=0;i<128;i++){r[i]=previous[i]^reference[i];keep[i]=withXor?r[i]^next[i]:r[i];}
            for(int i=0;i<8;i++)Round(r,16*i,16*i+1,16*i+2,16*i+3,16*i+4,16*i+5,16*i+6,16*i+7,16*i+8,16*i+9,16*i+10,16*i+11,16*i+12,16*i+13,16*i+14,16*i+15);
            for(int i=0;i<8;i++)Round(r,2*i,2*i+1,2*i+16,2*i+17,2*i+32,2*i+33,2*i+48,2*i+49,2*i+64,2*i+65,2*i+80,2*i+81,2*i+96,2*i+97,2*i+112,2*i+113);
            for(int i=0;i<128;i++)next[i]=keep[i]^r[i];
        }
        static void Round(ulong[] v,int a0,int a1,int a2,int a3,int a4,int a5,int a6,int a7,int a8,int a9,int a10,int a11,int a12,int a13,int a14,int a15) {
            GB(v,a0,a4,a8,a12);GB(v,a1,a5,a9,a13);GB(v,a2,a6,a10,a14);GB(v,a3,a7,a11,a15);
            GB(v,a0,a5,a10,a15);GB(v,a1,a6,a11,a12);GB(v,a2,a7,a8,a13);GB(v,a3,a4,a9,a14);
        }
        static ulong BlaMka(ulong x,ulong y) {return x+y+2*((x&0xFFFFFFFFUL)*(y&0xFFFFFFFFUL));}
        static ulong Rotr(ulong x,int n) {return (x>>n)|(x<<(64-n));}
        static void GB(ulong[] v,int a,int b,int c,int d) {
            v[a]=BlaMka(v[a],v[b]);v[d]=Rotr(v[d]^v[a],32);
            v[c]=BlaMka(v[c],v[d]);v[b]=Rotr(v[b]^v[c],24);
            v[a]=BlaMka(v[a],v[b]);v[d]=Rotr(v[d]^v[a],16);
            v[c]=BlaMka(v[c],v[d]);v[b]=Rotr(v[b]^v[c],63);
        }
        // H' of RFC 9106 section 3.3: a hash of any length built from BLAKE2b.
        static byte[] LongHash(int length,byte[] input) {
            var output=new byte[length];
            if(length<=64) {
                var h=new Blake2b(length);h.Update(Le32(length));h.Update(input);return h.Final();
            }
            int r=(length+31)/32-2;
            var first=new Blake2b(64);first.Update(Le32(length));first.Update(input);
            byte[] v=first.Final();
            Buffer.BlockCopy(v,0,output,0,32);
            for(int i=1;i<r;i++) {
                var next=new Blake2b(64);next.Update(v);v=next.Final();
                Buffer.BlockCopy(v,0,output,i*32,32);
            }
            var tail=new Blake2b(length-32*r);tail.Update(v);
            Buffer.BlockCopy(tail.Final(),0,output,32*r,length-32*r);
            return output;
        }
        static void ToWords(byte[] bytes,ulong[] words) {for(int i=0;i<128;i++)words[i]=BitConverter.ToUInt64(bytes,i*8);}
        static byte[] FromWords(ulong[] words) {var b=new byte[1024];for(int i=0;i<128;i++)Buffer.BlockCopy(BitConverter.GetBytes(words[i]),0,b,i*8,8);return b;}
        internal static byte[] Le32(int value) {return BitConverter.GetBytes(value);}

        // ---------------- BLAKE2b (RFC 7693), no key ----------------
        internal sealed class Blake2b {
            static readonly ulong[] IV={0x6a09e667f3bcc908UL,0xbb67ae8584caa73bUL,0x3c6ef372fe94f82bUL,0xa54ff53a5f1d36f1UL,
                0x510e527fade682d1UL,0x9b05688c2b3e6c1fUL,0x1f83d9abfb41bd6bUL,0x5be0cd19137e2179UL};
            static readonly byte[,] Sigma={
                {0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15},{14,10,4,8,9,15,13,6,1,12,0,2,11,7,5,3},
                {11,8,12,0,5,2,15,13,10,14,3,6,7,1,9,4},{7,9,3,1,13,12,11,14,2,6,5,10,4,0,15,8},
                {9,0,5,7,2,4,10,15,14,1,11,12,6,8,3,13},{2,12,6,10,0,11,8,3,4,13,7,5,15,14,1,9},
                {12,5,1,15,14,13,4,10,0,7,6,3,9,2,8,11},{13,11,7,14,12,1,3,9,5,0,15,4,8,6,2,10},
                {6,15,14,9,11,3,0,8,12,2,13,7,1,4,10,5},{10,2,8,4,7,6,1,5,15,11,9,14,3,12,13,0}};
            readonly ulong[] h=new ulong[8];
            readonly byte[] buffer=new byte[128];
            readonly int outLength;
            int filled;ulong t0,t1;
            internal Blake2b(int outLength) {
                if(outLength<1 || outLength>64)throw new ArgumentOutOfRangeException("outLength");
                this.outLength=outLength;
                Array.Copy(IV,h,8);
                h[0]^=0x01010000UL^(ulong)outLength;
            }
            internal void Update(byte[] data) {Update(data,0,data.Length);}
            internal void Update(byte[] data,int offset,int count) {
                while(count>0) {
                    if(filled==128) {Add(128);Compress(false);filled=0;}
                    int take=Math.Min(128-filled,count);
                    Buffer.BlockCopy(data,offset,buffer,filled,take);
                    filled+=take;offset+=take;count-=take;
                }
            }
            void Add(int n) {t0+=(ulong)n;if(t0<(ulong)n)t1++;}
            internal byte[] Final() {
                Add(filled);
                for(int i=filled;i<128;i++)buffer[i]=0;
                Compress(true);
                var all=new byte[64];
                for(int i=0;i<8;i++)Buffer.BlockCopy(BitConverter.GetBytes(h[i]),0,all,i*8,8);
                var result=new byte[outLength];Buffer.BlockCopy(all,0,result,0,outLength);
                return result;
            }
            void Compress(bool last) {
                var m=new ulong[16];var v=new ulong[16];
                for(int i=0;i<16;i++)m[i]=BitConverter.ToUInt64(buffer,i*8);
                for(int i=0;i<8;i++){v[i]=h[i];v[i+8]=IV[i];}
                v[12]^=t0;v[13]^=t1;
                if(last)v[14]=~v[14];
                for(int r=0;r<12;r++) {
                    int s=r%10;
                    G(v,0,4,8,12,m[Sigma[s,0]],m[Sigma[s,1]]);G(v,1,5,9,13,m[Sigma[s,2]],m[Sigma[s,3]]);
                    G(v,2,6,10,14,m[Sigma[s,4]],m[Sigma[s,5]]);G(v,3,7,11,15,m[Sigma[s,6]],m[Sigma[s,7]]);
                    G(v,0,5,10,15,m[Sigma[s,8]],m[Sigma[s,9]]);G(v,1,6,11,12,m[Sigma[s,10]],m[Sigma[s,11]]);
                    G(v,2,7,8,13,m[Sigma[s,12]],m[Sigma[s,13]]);G(v,3,4,9,14,m[Sigma[s,14]],m[Sigma[s,15]]);
                }
                for(int i=0;i<8;i++)h[i]^=v[i]^v[i+8];
            }
            static void G(ulong[] v,int a,int b,int c,int d,ulong x,ulong y) {
                v[a]=v[a]+v[b]+x;v[d]=Rotr(v[d]^v[a],32);
                v[c]=v[c]+v[d];v[b]=Rotr(v[b]^v[c],24);
                v[a]=v[a]+v[b]+y;v[d]=Rotr(v[d]^v[a],16);
                v[c]=v[c]+v[d];v[b]=Rotr(v[b]^v[c],63);
            }
        }

        // ---------------- AES-256-GCM through Windows CNG ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo {
            public int cbSize,dwInfoVersion;
            public IntPtr pbNonce;public int cbNonce;
            public IntPtr pbAuthData;public int cbAuthData;
            public IntPtr pbTag;public int cbTag;
            public IntPtr pbMacContext;public int cbMacContext;
            public int cbAAD;public long cbData;public int dwFlags;
        }
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] static extern uint BCryptOpenAlgorithmProvider(out IntPtr alg,string id,string impl,int flags);
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] static extern uint BCryptSetProperty(IntPtr obj,string property,byte[] input,int size,int flags);
        [DllImport("bcrypt.dll")] static extern uint BCryptGenerateSymmetricKey(IntPtr alg,out IntPtr key,IntPtr keyObject,int keyObjectSize,byte[] secret,int secretSize,int flags);
        [DllImport("bcrypt.dll")] static extern uint BCryptEncrypt(IntPtr key,byte[] input,int inputSize,ref AuthInfo info,IntPtr iv,int ivSize,byte[] output,int outputSize,out int result,int flags);
        [DllImport("bcrypt.dll")] static extern uint BCryptDecrypt(IntPtr key,byte[] input,int inputSize,ref AuthInfo info,IntPtr iv,int ivSize,byte[] output,int outputSize,out int result,int flags);
        [DllImport("bcrypt.dll")] static extern uint BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern uint BCryptCloseAlgorithmProvider(IntPtr alg,int flags);
        const uint AuthTagMismatch=0xC000A002;

        // One key, used for many pieces. Dispose closes it.
        internal sealed class Gcm : IDisposable {
            IntPtr alg,key;
            internal Gcm(byte[] secret) {
                if(secret.Length!=32)throw new ArgumentException("key");
                Check(BCryptOpenAlgorithmProvider(out alg,"AES",null,0));
                var mode=Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(alg,"ChainingMode",mode,mode.Length,0));
                Check(BCryptGenerateSymmetricKey(alg,out key,IntPtr.Zero,0,secret,secret.Length,0));
            }
            static void Check(uint status) {if(status!=0)throw new CryptographicException("CNG 0x"+status.ToString("X8"));}
            // Returns ciphertext + 16-byte tag.
            internal byte[] Seal(byte[] nonce,byte[] aad,byte[] plain,int count) {
                var output=new byte[count+16];
                var tag=new byte[16];var cipher=new byte[count];
                var input=plain;
                if(plain.Length!=count){input=new byte[count];Buffer.BlockCopy(plain,0,input,0,count);}
                Run(true,nonce,aad,input,cipher,tag);
                Buffer.BlockCopy(cipher,0,output,0,count);Buffer.BlockCopy(tag,0,output,count,16);
                return output;
            }
            // Null when the piece was changed or the key is wrong.
            internal byte[] Open(byte[] nonce,byte[] aad,byte[] sealedData) {
                if(sealedData.Length<16)return null;
                int count=sealedData.Length-16;
                var cipher=new byte[count];var tag=new byte[16];var plain=new byte[count];
                Buffer.BlockCopy(sealedData,0,cipher,0,count);Buffer.BlockCopy(sealedData,count,tag,0,16);
                return Run(false,nonce,aad,cipher,plain,tag)?plain:null;
            }
            bool Run(bool encrypt,byte[] nonce,byte[] aad,byte[] input,byte[] output,byte[] tag) {
                var pins=new[]{GCHandle.Alloc(nonce,GCHandleType.Pinned),GCHandle.Alloc(aad,GCHandleType.Pinned),GCHandle.Alloc(tag,GCHandleType.Pinned)};
                try {
                    var info=new AuthInfo {
                        cbSize=Marshal.SizeOf(typeof(AuthInfo)),dwInfoVersion=1,
                        pbNonce=pins[0].AddrOfPinnedObject(),cbNonce=nonce.Length,
                        pbAuthData=pins[1].AddrOfPinnedObject(),cbAuthData=aad.Length,
                        pbTag=pins[2].AddrOfPinnedObject(),cbTag=tag.Length
                    };
                    int done;
                    uint status=encrypt?BCryptEncrypt(key,input,input.Length,ref info,IntPtr.Zero,0,output,output.Length,out done,0)
                        :BCryptDecrypt(key,input,input.Length,ref info,IntPtr.Zero,0,output,output.Length,out done,0);
                    if(!encrypt && status==AuthTagMismatch)return false;
                    Check(status);
                    return true;
                } finally {foreach(var p in pins)p.Free();}
            }
            public void Dispose() {
                if(key!=IntPtr.Zero){BCryptDestroyKey(key);key=IntPtr.Zero;}
                if(alg!=IntPtr.Zero){BCryptCloseAlgorithmProvider(alg,0);alg=IntPtr.Zero;}
            }
        }

        // The password as bytes: Unicode NFC (the same letters typed on another keyboard give the same bytes), UTF-8.
        internal static byte[] PasswordBytes(string password) {
            return new UTF8Encoding(false).GetBytes(password.Normalize(NormalizationForm.FormC));
        }
        internal static byte[] Random(int count) {
            var bytes=new byte[count];
            using(var rng=new RNGCryptoServiceProvider())rng.GetBytes(bytes);
            return bytes;
        }
    }
}
