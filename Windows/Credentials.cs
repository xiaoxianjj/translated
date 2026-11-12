using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Translated
{
    internal static class Credentials
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Credential
        {
            public uint Flags, Type;
            public string TargetName, Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint BlobSize;
            public IntPtr Blob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")] static extern void CredFree(IntPtr credential);

        public static bool Exists(Provider provider)
        {
            IntPtr record;
            if (CredRead(provider.CredentialId(), 1, 0, out record)) { CredFree(record); return true; }
            if (Marshal.GetLastWin32Error() == 1168) return false;
            throw new Win32Exception();
        }

        public static string Load(Provider provider)
        {
            IntPtr p;
            if (!CredRead(provider.CredentialId(), 1, 0, out p))
            {
                if (Marshal.GetLastWin32Error() == 1168) return "";
                throw new Win32Exception();
            }
            try
            {
                var data = Marshal.PtrToStructure<Credential>(p);
                return Marshal.PtrToStringUni(data.Blob, (int)data.BlobSize / 2) ?? "";
            }
            finally { CredFree(p); }
        }

        public static void Save(Provider provider, string key)
        {
            if (key.Length > 1280) throw new InvalidOperationException("API 密钥过长。");
            var blob = Marshal.StringToCoTaskMemUni(key);
            try
            {
                var record = new Credential { Type = 1, Persist = 2, TargetName = provider.CredentialId(),
                    UserName = provider.Name, Comment = "Translated", BlobSize = (uint)Encoding.Unicode.GetByteCount(key), Blob = blob };
                if (!CredWrite(ref record, 0)) throw new Win32Exception();
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
        }

        public static void Remove(Provider provider)
        {
            if (!CredDelete(provider.CredentialId(), 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new Win32Exception();
        }
    }
}
