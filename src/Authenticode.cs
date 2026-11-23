// Authenticode check of a file through WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2, no UI, no revocation check over
// the network). Used before tcpkg.exe outside the program folders is started with administrator rights (TcpkgTrust.cs).
//
// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace TcAgent;

static class Authenticode
{
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;          // 2 = WTD_UI_NONE
        public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
        public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;         // 0x1000 = WTD_CACHE_ONLY_URL_RETRIEVAL (no network)
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    /// <summary>(true, "valid") when the file carries a valid Authenticode signature, otherwise (false, the reason).</summary>
    public static (bool ok, string info) Verify(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1,
                pFile = pFile, dwStateAction = 0, dwProvFlags = 0x1000
            };
            int hr = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return hr switch
            {
                0 => (true, "valid"),
                unchecked((int)0x800B0100) => (false, "not signed"),
                unchecked((int)0x80096010) => (false, "the file was changed after signing"),
                unchecked((int)0x800B0109) => (false, "the certificate chain ends in an untrusted root"),
                unchecked((int)0x800B0101) => (false, "the certificate has expired"),
                _ => (false, $"WinVerifyTrust 0x{hr:X8}")
            };
        }
        catch (Exception ex) { return (false, "check failed: " + ex.Message); }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
