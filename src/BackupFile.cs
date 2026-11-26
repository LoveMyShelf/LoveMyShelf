using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace LoveMyShelf {
    // A backup file (.lmsbackup):
    //   header (54 bytes, not locked): "LMSBAK1\0", version 1, Argon2id memory/passes/lanes, salt 16, base nonce 12, piece size
    //   pieces: [length u32][locked bytes + 16-byte tag], each locked with AES-256-GCM.
    // Every piece is tied to the whole header, its number and whether it is the last one (the "extra data" GCM checks),
    // so a piece that was changed, swapped, repeated, cut off or added after the end is found.
    // Piece 0 holds the list (manifest, XML); the next pieces hold the files one after another:
    //   [kind u8: 1 file, 0 end][name length u16][name UTF-8][length i64][bytes]
    internal static class BackupFile {
        internal const string Extension=".lmsbackup";
        static readonly byte[] Magic=Encoding.ASCII.GetBytes("LMSBAK1\0");
        const int HeaderLength=54;
        internal const int PieceSize=4*1024*1024;
        internal const int MemoryKiB=65536,Passes=3,Lanes=4;

        internal sealed class WrongPasswordException : Exception {
            internal WrongPasswordException():base("wrong password or damaged start") {}
        }
        internal sealed class DamagedException : Exception {
            internal DamagedException(string why):base(why) {}
        }

        static byte[] Aad(byte[] headerHash,long index,bool last) {
            var aad=new byte[41];
            Buffer.BlockCopy(headerHash,0,aad,0,32);
            for(int i=0;i<8;i++)aad[32+i]=(byte)(index>>(56-8*i));
            aad[40]=(byte)(last?1:0);
            return aad;
        }
        static byte[] Nonce(byte[] baseNonce,long index) {
            var nonce=(byte[])baseNonce.Clone();
            for(int i=0;i<8;i++)nonce[4+i]^=(byte)(index>>(56-8*i));
            return nonce;
        }
        internal static byte[] Key(string password,byte[] salt) {
            return BackupCrypto.Argon2id(BackupCrypto.PasswordBytes(password),salt,MemoryKiB,Passes,Lanes,32);
        }

        internal sealed class Writer : IDisposable {
            readonly Stream file;
            readonly BackupCrypto.Gcm gcm;
            readonly byte[] headerHash,baseNonce;
            readonly MemoryStream pending=new MemoryStream();
            long index;
            internal long Written {get;private set;}
            internal Writer(Stream file,string password,XElement manifest) {
                this.file=file;
                byte[] salt=BackupCrypto.Random(16);baseNonce=BackupCrypto.Random(12);
                var header=new byte[HeaderLength];
                Buffer.BlockCopy(Magic,0,header,0,8);
                header[8]=1;header[9]=0;
                Buffer.BlockCopy(BitConverter.GetBytes(MemoryKiB),0,header,10,4);
                Buffer.BlockCopy(BitConverter.GetBytes(Passes),0,header,14,4);
                Buffer.BlockCopy(BitConverter.GetBytes(Lanes),0,header,18,4);
                Buffer.BlockCopy(salt,0,header,22,16);
                Buffer.BlockCopy(baseNonce,0,header,38,12);
                Buffer.BlockCopy(BitConverter.GetBytes(PieceSize),0,header,50,4);
                using(var sha=SHA256.Create())headerHash=sha.ComputeHash(header);
                file.Write(header,0,header.Length);
                byte[] key=Key(password,salt);
                gcm=new BackupCrypto.Gcm(key);
                Array.Clear(key,0,key.Length);
                // The list is packed (GZip): the XML repeats a lot, so even a very big library fits in one piece.
                byte[] list;
                using(var packed=new MemoryStream()) {
                    using(var zip=new System.IO.Compression.GZipStream(packed,System.IO.Compression.CompressionLevel.Optimal,true)) {
                        byte[] xml=new UTF8Encoding(false).GetBytes(manifest.ToString(SaveOptions.DisableFormatting));
                        zip.Write(xml,0,xml.Length);
                    }
                    list=packed.ToArray();
                }
                if(list.Length>PieceSize)throw new InvalidDataException("manifest too large");
                Emit(list,list.Length,false);
            }
            void Emit(byte[] plain,int count,bool last) {
                byte[] sealedData=gcm.Seal(Nonce(baseNonce,index),Aad(headerHash,index,last),plain,count);
                file.Write(BitConverter.GetBytes(sealedData.Length),0,4);
                file.Write(sealedData,0,sealedData.Length);
                Written+=4+sealedData.Length;
                index++;
            }
            void Put(byte[] data,int offset,int count) {
                while(count>0) {
                    int take=(int)Math.Min(count,PieceSize-pending.Length);
                    pending.Write(data,offset,take);offset+=take;count-=take;
                    if(pending.Length==PieceSize) {Emit(pending.GetBuffer(),PieceSize,false);pending.SetLength(0);}
                }
            }
            internal void AddFile(string name,Stream data,long length,System.Threading.CancellationToken token) {
                byte[] nameBytes=new UTF8Encoding(false).GetBytes(name);
                if(nameBytes.Length>4000)throw new PathTooLongException(name);
                var head=new byte[1+2+nameBytes.Length+8];
                head[0]=1;
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)nameBytes.Length),0,head,1,2);
                Buffer.BlockCopy(nameBytes,0,head,3,nameBytes.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(length),0,head,3+nameBytes.Length,8);
                Put(head,0,head.Length);
                var buffer=new byte[1<<20];
                long left=length;
                while(left>0) {
                    token.ThrowIfCancellationRequested();
                    int read=data.Read(buffer,0,(int)Math.Min(buffer.Length,left));
                    if(read<=0)throw new IOException("The file got shorter while it was read: "+name);
                    Put(buffer,0,read);left-=read;
                }
            }
            internal void AddBytes(string name,byte[] bytes) {
                using(var data=new MemoryStream(bytes))AddFile(name,data,bytes.Length,System.Threading.CancellationToken.None);
            }
            internal void Finish() {
                Put(new byte[]{0},0,1);
                Emit(pending.GetBuffer(),(int)pending.Length,true);
                pending.SetLength(0);
                file.Flush();
            }
            public void Dispose() {gcm.Dispose();}
        }

        // Reads a backup: Open checks the password with piece 0 and returns the list; Entries then goes through every
        // file once, checking every piece. Nothing that fails a check is ever handed out.
        internal sealed class Reader : IDisposable {
            readonly Stream file;
            BackupCrypto.Gcm gcm;
            byte[] headerHash,baseNonce;
            long index;
            byte[] current;int position;bool sawLast;
            internal XElement Manifest {get;private set;}
            internal long Length {get{return file.Length;}}
            internal long Position {get{return file.Position;}}
            internal Reader(Stream file,string password) {
                this.file=file;
                var header=ReadExactly(HeaderLength,true);
                for(int i=0;i<8;i++)if(header[i]!=Magic[i])throw new DamagedException("not a Love My Shelf backup");
                if(header[8]!=1 || header[9]!=0)throw new DamagedException("newer backup format");
                // Only the settings this version writes: a changed header can't make the computer do huge work.
                if(BitConverter.ToInt32(header,10)!=MemoryKiB || BitConverter.ToInt32(header,14)!=Passes ||
                    BitConverter.ToInt32(header,18)!=Lanes || BitConverter.ToInt32(header,50)!=PieceSize)
                    throw new DamagedException("unknown lock settings");
                var salt=new byte[16];Buffer.BlockCopy(header,22,salt,0,16);
                baseNonce=new byte[12];Buffer.BlockCopy(header,38,baseNonce,0,12);
                using(var sha=SHA256.Create())headerHash=sha.ComputeHash(header);
                byte[] key=Key(password,salt);
                gcm=new BackupCrypto.Gcm(key);
                Array.Clear(key,0,key.Length);
                bool last;
                byte[] list=NextPiece(out last,true);
                if(last)throw new DamagedException("no files");
                try {
                    if(list.Length>=2 && list[0]==0x1f && list[1]==0x8b) {
                        using(var zip=new System.IO.Compression.GZipStream(new MemoryStream(list),System.IO.Compression.CompressionMode.Decompress))
                        using(var text=new StreamReader(zip,new UTF8Encoding(false,true)))
                            Manifest=XElement.Parse(text.ReadToEnd());
                    } else Manifest=XElement.Parse(new UTF8Encoding(false,true).GetString(list));
                }
                catch(Exception) {throw new DamagedException("list unreadable");}
            }
            byte[] ReadExactly(int count,bool start) {
                var data=new byte[count];int used=0;
                while(used<count) {
                    int read=file.Read(data,used,count-used);
                    if(read<=0)throw start?(Exception)new DamagedException("too short"):new DamagedException("cut off");
                    used+=read;
                }
                return data;
            }
            byte[] NextPiece(out bool last,bool first) {
                int length=BitConverter.ToInt32(ReadExactly(4,first),0);
                if(length<16 || length>PieceSize+16)throw first?(Exception)new WrongPasswordException():new DamagedException("bad piece length");
                byte[] sealedData=ReadExactly(length,first);
                byte[] plain=gcm.Open(Nonce(baseNonce,index),Aad(headerHash,index,false),sealedData);
                last=false;
                if(plain==null) {
                    plain=gcm.Open(Nonce(baseNonce,index),Aad(headerHash,index,true),sealedData);
                    if(plain==null)throw first?(Exception)new WrongPasswordException():new DamagedException("piece "+index+" changed");
                    last=true;
                }
                index++;
                return plain;
            }
            int ReadByte() {
                while(current==null || position>=current.Length) {
                    if(sawLast)throw new DamagedException("ended early");
                    bool last;current=NextPiece(out last,false);position=0;
                    if(last)sawLast=true;
                }
                return current[position++];
            }
            void Read(byte[] buffer,int offset,int count) {
                while(count>0) {
                    if(current==null || position>=current.Length){ReadByte();position--;}
                    int take=Math.Min(count,current.Length-position);
                    Buffer.BlockCopy(current,position,buffer,offset,take);
                    position+=take;offset+=take;count-=take;
                }
            }
            // Calls take(name,length,read) for each file; read(buffer,offset,count) fills exactly count bytes.
            // take must read the whole file (or call skip). Returns after checking the end mark and the last piece.
            internal void Entries(Action<string,long,Func<byte[],int,int,int>> take,System.Threading.CancellationToken token) {
                var small=new byte[8];
                while(true) {
                    token.ThrowIfCancellationRequested();
                    int kind=ReadByte();
                    if(kind==0)break;
                    if(kind!=1)throw new DamagedException("unknown entry");
                    Read(small,0,2);
                    int nameLength=BitConverter.ToUInt16(small,0);
                    var nameBytes=new byte[nameLength];Read(nameBytes,0,nameLength);
                    string name=new UTF8Encoding(false,true).GetString(nameBytes);
                    Read(small,0,8);
                    long length=BitConverter.ToInt64(small,0);
                    if(length<0)throw new DamagedException("bad length");
                    long left=length;
                    take(name,length,(buffer,offset,count)=>{
                        if(count>left)count=(int)left;
                        Read(buffer,offset,count);left-=count;return count;
                    });
                    var skip=new byte[1<<16];
                    while(left>0){int n=(int)Math.Min(skip.Length,left);Read(skip,0,n);left-=n;}
                }
                // The end mark must be the last thing, in the piece marked last, with nothing after it.
                if(current!=null && position<current.Length)throw new DamagedException("data after the end");
                while(!sawLast) {
                    bool last;var piece=NextPiece(out last,false);
                    if(piece.Length>0)throw new DamagedException("data after the end");
                    if(last)sawLast=true;
                }
                if(file.Position!=file.Length)throw new DamagedException("data after the last piece");
            }
            public void Dispose() {if(gcm!=null)gcm.Dispose();}
        }
    }
}
