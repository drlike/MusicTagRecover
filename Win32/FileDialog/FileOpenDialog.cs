using System;
using System.Runtime.InteropServices;

namespace MusicTagRecover.Win32.FileDialog;

/// <summary>
/// IFileDialog interface — native IFileDialog.
/// GUID: {42F85136-DB7E-439C-85F1-E4075D135FC8}
/// </summary>
[ComImport]
[Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialog
{
    // IModalWindow
    [PreserveSig]
    int Show([In, Optional] IntPtr hwndOwner);

    // IFileDialog
    void SetFileTypes([In] uint cFileTypes,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgFilterSpec);

    void SetFileTypeIndex([In] uint iFileType);

    uint GetFileTypeIndex(out uint piFileType);

    void Advise([In, MarshalAs(UnmanagedType.Interface)] IntPtr pfde,
        out uint pdwCookie);

    void Unadvise([In] uint dwCookie);

    void SetOptions([In] FOS fos);

    void GetOptions(out FOS fos);

    void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);

    void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);

    void GetFolder([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    void GetCurrentSelection([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);

    void GetFileName([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszName);

    void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

    void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

    void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void GetResult([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    void AddPlace([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi,
        [In] uint fdap);

    void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

    void Close([In, MarshalAs(UnmanagedType.Error)] int hr);

    void SetClientGuid([In] ref Guid guid);

    void ClearClientData();

    void SetFilter([In, MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
}

/// <summary>
/// IFileOpenDialog interface — native IFileOpenDialog.
/// GUID: {D57C7288-D4AD-4768-BE02-9D969532D960}
/// </summary>
[ComImport]
[Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOpenDialog
{
    // IModalWindow
    [PreserveSig]
    int Show([In, Optional] IntPtr hwndOwner);

    // IFileDialog
    void SetFileTypes([In] uint cFileTypes,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgFilterSpec);

    void SetFileTypeIndex([In] uint iFileType);

    uint GetFileTypeIndex(out uint piFileType);

    void Advise([In, MarshalAs(UnmanagedType.Interface)] IntPtr pfde,
        out uint pdwCookie);

    void Unadvise([In] uint dwCookie);

    void SetOptions([In] FOS fos);

    void GetOptions(out FOS fos);

    void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);

    void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);

    void GetFolder([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    void GetCurrentSelection([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);

    void GetFileName([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszName);

    void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

    void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

    void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void GetResult([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    void AddPlace([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi,
        [In] uint fdap);

    void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

    void Close([In, MarshalAs(UnmanagedType.Error)] int hr);

    void SetClientGuid([In] ref Guid guid);

    void ClearClientData();

    void SetFilter([In, MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);

    // IFileOpenDialog
    void GetResults([Out, MarshalAs(UnmanagedType.Interface)] out IShellItemArray ppenum);

    void GetSelectedItems([Out, MarshalAs(UnmanagedType.Interface)] out IShellItemArray ppsai);
}

/// <summary>
/// IFileDialogCustomize interface — used to add controls (e.g. checkbox) to the file dialog.
/// GUID: {8016b7b3-3d49-4504-a0aa-2a37494e606f}
/// </summary>
[ComImport]
[Guid("8016b7b3-3d49-4504-a0aa-2a37494e606f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialogCustomize
{
    // IFileDialogCustomize (independent interface — starts with its own methods)
    void EnableOpenDropDown([In] int dwIDCtl);

    void AddMenu([In] int dwIDCtl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void AddPushButton([In] int dwIDCtl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void AddComboBox([In] int dwIDCtl);

    void AddRadioButtonList([In] int dwIDCtl);

    void AddCheckButton([In] int dwIDCtl,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel,
        [In] bool bChecked);

    void AddEditBox([In] int dwIDCtl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

    void AddSeparator([In] int dwIDCtl);

    void AddText([In] int dwIDCtl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

    void SetControlLabel([In] int dwIDCtl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void GetControlState([In] int dwIDCtl, out int pdwState);

    void SetControlState([In] int dwIDCtl, [In] int dwState);

    void GetEditBoxText([In] int dwIDCtl, [Out] IntPtr ppszText);

    void SetEditBoxText([In] int dwIDCtl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszText);

    void GetCheckButtonState([In] int dwIDCtl, out bool pbChecked);

    void SetCheckButtonState([In] int dwIDCtl, [In] bool bChecked);

    void AddControlItem([In] int dwIDCtl, [In] int dwIDItem,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void RemoveControlItem([In] int dwIDCtl, [In] int dwIDItem);

    void RemoveAllControlItems([In] int dwIDCtl);

    void GetControlItemState([In] int dwIDCtl, [In] int dwIDItem, out int pdwState);

    void SetControlItemState([In] int dwIDCtl, [In] int dwIDItem, [In] int dwState);

    void GetSelectedControlItem([In] int dwIDCtl, out int pdwIDItem);

    void SetSelectedControlItem([In] int dwIDCtl, [In] int dwIDItem);

    void StartVisualGroup([In] int dwIDCtl,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

    void EndVisualGroup();

    void MakeProminent([In] int dwIDCtl);
}

/// <summary>
/// FileOpenDialog RCW — coclass for the native File Open dialog.
/// CLSID: {DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7}
/// </summary>
[ComImport]
[Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
[ClassInterface(ClassInterfaceType.None)]
internal class FileOpenDialogRCW
{
}
