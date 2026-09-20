// Native folder picker via IFileOpenDialog (COM), no WinForms dependency
// so the app stays trim-compatible. Requires STA thread (see Main).
// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace OpencodePortable;

internal static class NativeFolderDialog
{
    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>
    /// Non-UI smoke test: instantiates the COM dialog and exercises
    /// GetOptions/SetOptions without showing anything. Returns true on success.
    /// Catches trimming/vtable regressions that automated runs would miss
    /// (Show itself needs a human click).
    /// </summary>
    public static bool SelfTest(out string detail)
    {
        try
        {
            var dlg = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                Marshal.ThrowExceptionForHR(dlg.GetOptions(out uint opts));
                Marshal.ThrowExceptionForHR(dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM));
                Marshal.ThrowExceptionForHR(dlg.SetTitle("self-test"));
                detail = $"COM OK (options=0x{opts:X})";
                return true;
            }
        finally { try { Marshal.ReleaseComObject(dlg); } catch { } }
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static string? PickFolder(string title, string initialPath)
    {
        var trace = new List<string>();
        IFileOpenDialog dlg = null!;
        try
        {
            try
            {
                // Created INSIDE the traced try so a coclass failure also gets a
                // PICK-ERROR wrapper instead of escaping raw.
                dlg = (IFileOpenDialog)new FileOpenDialog();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "folder dialog failed — COM trace: coclass-create failed", ex);
            }
            try
            {
                Marshal.ThrowExceptionForHR(Check(dlg.GetOptions(out uint opts), "GetOptions", trace));
                Marshal.ThrowExceptionForHR(Check(dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM), "SetOptions", trace));
                Marshal.ThrowExceptionForHR(Check(dlg.SetTitle(title), "SetTitle", trace));
                if (Directory.Exists(initialPath))
                {
                    Guid iid = typeof(IShellItem).GUID;
                    int hr = SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref iid, out IntPtr folderPtr);
                    trace.Add($"SHCreateItemFromParsingName=0x{hr:X8}");
                    if (hr >= 0 && folderPtr != IntPtr.Zero)
                    {
                        // hr>=0 guarantees a valid pointer: safe to wrap; on wrap
                        // failure release the owned ref before rethrowing.
                        object folderObj;
                        try { folderObj = Marshal.GetObjectForIUnknown(folderPtr); }
                        catch { Marshal.Release(folderPtr); throw; }
                        Marshal.Release(folderPtr);
                        try
                        {
                            if (folderObj is IShellItem folder)
                            {
                                try { dlg.SetFolder(folder); trace.Add("SetFolder=OK(ignored-hr)"); }
                                catch (Exception ex) { trace.Add($"SetFolder threw {ex.GetType().Name} (ignored)"); }
                            }
                            else trace.Add("SetFolder skipped: not IShellItem");
                        }
                        finally { Marshal.ReleaseComObject(folderObj); }
                    }
                }
                int show = Check(dlg.Show(IntPtr.Zero), "Show", trace);
                if (show == HRESULT_CANCELLED) return null; // user pressed Cancel/ESC
                Marshal.ThrowExceptionForHR(show);
                // Raw pointers: the runtime never wraps a failed/garbage out-param,
                // so a native failure surfaces as HRESULT, never as AV. On failure
                // just forget the pointer (never Release garbage).
                IntPtr itemPtr = IntPtr.Zero;
                int res = Check(dlg.GetResult(out itemPtr), "GetResult", trace);
                if (res < 0)
                {
                    itemPtr = IntPtr.Zero;
                    trace.Add("GetResult retry...");
                    res = Check(dlg.GetResult(out itemPtr), "GetResult#2", trace);
                }
                if (res < 0)
                {
                    itemPtr = IntPtr.Zero;
                    // Second road, same dialog: IShellItemArray path. Some namespace
                    // locations (VM shared folders, odd redirectors) fail GetResult
                    // but serve the array fine.
                    trace.Add("trying IShellItemArray path...");
                    int resArr = Check(dlg.GetResults(out IntPtr arrPtr), "GetResults", trace);
                    if (resArr >= 0 && arrPtr != IntPtr.Zero)
                    {
                        object arrObj;
                        try { arrObj = Marshal.GetObjectForIUnknown(arrPtr); }
                        catch { Marshal.Release(arrPtr); throw; }
                        Marshal.Release(arrPtr);
                        try
                        {
                            if (arrObj is IShellItemArray arr)
                            {
                                Marshal.ThrowExceptionForHR(Check(arr.GetCount(out uint n), "Array.GetCount", trace));
                                trace.Add($"Array.Count={n}");
                                if (n > 0)
                                {
                                    int it = Check(arr.GetItemAt(0, out IntPtr p), "Array.GetItemAt", trace);
                                    Marshal.ThrowExceptionForHR(it);
                                    itemPtr = p;
                                }
                            }
                            else trace.Add("array is not IShellItemArray");
                        }
                        finally { Marshal.ReleaseComObject(arrObj); }
                    }
                    else
                    {
                        // Failed call: forget the out-pointer, never Release it.
                        Marshal.ThrowExceptionForHR(res); // original GetResult error wins
                    }
                }
                else
                {
                    Marshal.ThrowExceptionForHR(res);
                }
                if (itemPtr == IntPtr.Zero) throw new InvalidOperationException("dialog returned S_OK with null item");
                object itemObj;
                try { itemObj = Marshal.GetObjectForIUnknown(itemPtr); }
                catch { Marshal.Release(itemPtr); throw; }
                Marshal.Release(itemPtr);
                try
                {
                    if (itemObj is not IShellItem item)
                        throw new InvalidOperationException("returned item is not IShellItem");
                    Marshal.ThrowExceptionForHR(Check(item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr psz), "GetDisplayName", trace));
                    // A null display name is an error, NOT a user cancel (cancel is
                    // HRESULT_CANCELLED from Show, handled above).
                    string? path = null;
                    try { path = Marshal.PtrToStringUni(psz); }
                    finally { Marshal.FreeCoTaskMem(psz); }
                    if (path is null) throw new InvalidOperationException("GetDisplayName returned null string");
                    return path;
                }
                finally { Marshal.ReleaseComObject(itemObj); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Pinpoint: exactly which COM call failed, with full HR trace.
                throw new InvalidOperationException(
                    $"folder dialog failed — COM trace: {string.Join(" | ", trace)}", ex);
            }
        }
        finally { if (dlg is not null) { try { Marshal.ReleaseComObject(dlg); } catch { } } }
    }

    private static int Check(int hr, string method, List<string> trace)
    {
        trace.Add($"{method}=0x{hr:X8}");
        return hr;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes();
        void Compare();
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler();
        void GetPropertyStore();
        void GetPropertyDescriptionList();
        void GetAttributes();
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, out IntPtr ppsi);
        void EnumItems();
    }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        void SetDefaultFolder();
        [PreserveSig] int SetFolder(IShellItem psi);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName();
        void GetFileName();
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        // SDK order (ShObjIdl_core.h, verified): GetResult comes right AFTER
        // SetFileNameLabel, BEFORE AddPlace. Slots are positional — misplacing it
        // shifts every following slot and caused AV + phantom E_OUTOFMEMORY.
        [PreserveSig] int GetResult(out IntPtr ppsi);
        void AddPlace();
        void SetDefaultExtension();
        void Close();
        void SetClientGuid();
        void ClearClientData();
        void SetFilter();
        [PreserveSig] int GetResults(out IntPtr ppenum);
        void GetSelectedItems();
    }
}
