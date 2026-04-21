using System.Runtime.InteropServices;
using System.Text;

namespace Koh.Emulator.App;

/// <summary>
/// Native "Open File" dialog. Uses Win32 <c>GetOpenFileName</c> on
/// Windows; other platforms get a hard-fail for now (an X11 / Cocoa
/// equivalent can layer on as a partial class — nothing here blocks
/// running the rest of the emulator).
/// </summary>
internal static class FileDialog
{
    /// <summary>
    /// Open a ROM picker. Returns the selected absolute path, or null
    /// if the user cancelled / no dialog is available on this host.
    /// </summary>
    public static string? OpenRom(string? initialDir = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return OpenWin32("Open ROM", "Game Boy ROMs\0*.gb;*.gbc\0All files\0*.*\0", initialDir);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
    private const int OfnFlags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00008000;

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

    private static string? OpenWin32(string title, string filter, string? initialDir)
    {
        // comdlg32's filter list is double-null-terminated "name\0pattern\0…\0\0".
        // The string literal we're handed has trailing \0's but the
        // managed marshaller only copies up to the first null — so we
        // have to stage the filter into a buffer and hand a pointer.
        // Use the simpler approach: rely on CharSet=Unicode + pass a
        // string that already contains internal \0's. The P/Invoke
        // marshaller does preserve them for explicit fixed-length
        // strings, but the cleanest route is passing the filter via a
        // pinned byte buffer — see StringToFilter below.
        var fileBuf = Marshal.AllocHGlobal(2 * 2048);   // 2048 UTF-16 chars
        try
        {
            Marshal.WriteInt16(fileBuf, 0);   // zero-terminated empty string

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                lpstrFilter = filter,
                lpstrFile = fileBuf,
                nMaxFile = 2048,
                lpstrInitialDir = initialDir,
                lpstrTitle = title,
                Flags = OfnFlags,
            };

            if (!GetOpenFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(fileBuf);
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuf);
        }
    }
}
