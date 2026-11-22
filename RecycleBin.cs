using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LoveMyShelf {
    internal static class RecycleBin {
        [ComImport,Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOperation {
            void Advise(IFileOperationProgressSink sink,out uint cookie);
            void Unadvise(uint cookie);
            void SetOperationFlags(uint flags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)]string message);
            void SetProgressDialog(IntPtr dialog);
            void SetProperties(IntPtr properties);
            void SetOwnerWindow(IntPtr owner);
            void ApplyPropertiesToItem(IntPtr item);
            void ApplyPropertiesToItems(IntPtr items);
            void RenameItem(IntPtr item,IntPtr name,IntPtr sink);
            void RenameItems(IntPtr items,IntPtr name);
            void MoveItem(IntPtr item,IntPtr folder,IntPtr name,IntPtr sink);
            void MoveItems(IntPtr items,IntPtr folder);
            void CopyItem(IntPtr item,IntPtr folder,IntPtr name,IntPtr sink);
            void CopyItems(IntPtr items,IntPtr folder);
            void DeleteItem(IntPtr item,IFileOperationProgressSink sink);
            void DeleteItems(IntPtr items);
            void NewItem(IntPtr folder,uint attributes,IntPtr name,IntPtr template,IntPtr sink);
            void PerformOperations();
            void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)]out bool aborted);
        }
        [ComVisible(true),Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFileOperationProgressSink {
            void StartOperations(); void FinishOperations(int result);
            void PreRenameItem(uint flags,IntPtr item,IntPtr name);
            void PostRenameItem(uint flags,IntPtr item,IntPtr name,int result,IntPtr created);
            void PreMoveItem(uint flags,IntPtr item,IntPtr folder,IntPtr name);
            void PostMoveItem(uint flags,IntPtr item,IntPtr folder,IntPtr name,int result,IntPtr created);
            void PreCopyItem(uint flags,IntPtr item,IntPtr folder,IntPtr name);
            void PostCopyItem(uint flags,IntPtr item,IntPtr folder,IntPtr name,int result,IntPtr created);
            void PreDeleteItem(uint flags,IntPtr item);
            void PostDeleteItem(uint flags,IntPtr item,int result,IntPtr recycled);
            void PreNewItem(uint flags,IntPtr folder,IntPtr name);
            void PostNewItem(uint flags,IntPtr folder,IntPtr name,IntPtr template,uint attributes,int result,IntPtr created);
            void UpdateProgress(uint total,uint done); void ResetTimer(); void PauseTimer(); void ResumeTimer();
        }
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
        sealed class RecycleOnly : IFileOperationProgressSink {
            public bool Refused,Completed;
            public int Result;
            public void PreDeleteItem(uint flags,IntPtr item) {
                const uint recycleIfPossible=0x80;
                if((flags&recycleIfPossible)!=0)return;
                Refused=true;throw new COMException(L.T("The Recycle Bin is unavailable."),unchecked((int)0x80004004));
            }
            public void PostDeleteItem(uint flags,IntPtr item,int result,IntPtr recycled){Result=result;Completed=result>=0 && recycled!=IntPtr.Zero;}
            public void StartOperations(){} public void FinishOperations(int result){}
            public void PreRenameItem(uint flags,IntPtr item,IntPtr name){}
            public void PostRenameItem(uint flags,IntPtr item,IntPtr name,int result,IntPtr created){}
            public void PreMoveItem(uint flags,IntPtr item,IntPtr folder,IntPtr name){}
            public void PostMoveItem(uint flags,IntPtr item,IntPtr folder,IntPtr name,int result,IntPtr created){}
            public void PreCopyItem(uint flags,IntPtr item,IntPtr folder,IntPtr name){}
            public void PostCopyItem(uint flags,IntPtr item,IntPtr folder,IntPtr name,int result,IntPtr created){}
            public void PreNewItem(uint flags,IntPtr folder,IntPtr name){}
            public void PostNewItem(uint flags,IntPtr folder,IntPtr name,IntPtr template,uint attributes,int result,IntPtr created){}
            public void UpdateProgress(uint total,uint done){} public void ResetTimer(){} public void PauseTimer(){} public void ResumeTimer(){}
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
        static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out IntPtr item);

        internal static void MoveFile(string path,IntPtr owner) {
            if(!File.Exists(path))throw new FileNotFoundException(L.T("The original file could not be found."),path);
            var id=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            IntPtr item=IntPtr.Zero;
            var op=(IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
            var sink=new RecycleOnly();
            uint cookie=0;
            try {
                SHCreateItemFromParsingName(path,IntPtr.Zero,ref id,out item);
                const uint recycleOnDelete=0x80000,addUndoRecord=0x20000000,earlyFailure=0x100000,noErrorUI=0x400,silent=4,
                    allowUndo=0x40,noConfirmation=0x10;
                op.SetOwnerWindow(owner);op.SetOperationFlags(recycleOnDelete|allowUndo|addUndoRecord|earlyFailure|noErrorUI|silent|noConfirmation);
                op.Advise(sink,out cookie);op.DeleteItem(item,null);op.PerformOperations();
                bool aborted;op.GetAnyOperationsAborted(out aborted);
                if(sink.Result<0)Marshal.ThrowExceptionForHR(sink.Result);
                if(aborted || !sink.Completed)throw new IOException(L.T("Could not move the original to the Recycle Bin."));
            } catch(COMException) {
                if(sink.Refused)throw new IOException(L.T("The Windows Recycle Bin is unavailable for this location."));
                throw;
            } finally {
                if(cookie!=0)op.Unadvise(cookie);
                if(item!=IntPtr.Zero)Marshal.Release(item);
                Marshal.FinalReleaseComObject(op);
            }
        }
    }
}
