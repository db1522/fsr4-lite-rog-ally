using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RalFsrHelper.Core;

public enum SignatureCheck
{
    /// <summary>
    /// Valid signature from AMD, or Microsoft's WHQL driver signature on a file whose version info says AMD.
    /// AMD's driver DLLs (including amdxcffx64.dll from driver 26.10.1) carry the WHQL signature.
    /// </summary>
    AmdVerified,

    /// <summary>Valid signature, but not from AMD.</summary>
    OtherPublisher,

    /// <summary>No embedded signature, or the signature doesn't verify (file changed after signing).</summary>
    NotVerified,
}

/// <summary>Checks the embedded Authenticode signature on imported FSR 4 files.</summary>
public static class WinTrust
{
    private const string WhqlSigner = "Microsoft Windows Hardware Compatibility Publisher";

    public static SignatureCheck Check(string path)
    {
        if (!Verify(path)) return SignatureCheck.NotVerified;
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            if (cert.Subject.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                return SignatureCheck.AmdVerified;

            // WHQL-signed driver file: the signature proves the file is untouched, the version info names AMD.
            var company = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
            if (cert.Subject.Contains(WhqlSigner, StringComparison.OrdinalIgnoreCase) &&
                company.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                return SignatureCheck.AmdVerified;

            return SignatureCheck.OtherPublisher;
        }
        catch
        {
            return SignatureCheck.NotVerified;
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool Verify(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA { pFile = filePtr };
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, data) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;

        public WINTRUST_FILE_INFO()
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
            pcwszFilePath = "";
            hFile = IntPtr.Zero;
            pgKnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class WINTRUST_DATA
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>();
        public IntPtr pPolicyCallbackData = IntPtr.Zero;
        public IntPtr pSIPClientData = IntPtr.Zero;
        public uint dwUIChoice = 2;          // WTD_UI_NONE
        public uint fdwRevocationChecks = 0; // WTD_REVOKE_NONE
        public uint dwUnionChoice = 1;       // WTD_CHOICE_FILE
        public IntPtr pFile = IntPtr.Zero;
        public uint dwStateAction = 0;       // WTD_STATEACTION_IGNORE
        public IntPtr hWVTStateData = IntPtr.Zero;
        public IntPtr pwszURLReference = IntPtr.Zero;
        public uint dwProvFlags = 0x10;      // WTD_REVOCATION_CHECK_NONE, works offline
        public uint dwUIContext = 0;
        public IntPtr pSignatureSettings = IntPtr.Zero;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        [In] WINTRUST_DATA pWVTData);
}
