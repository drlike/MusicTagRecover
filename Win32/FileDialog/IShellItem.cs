using System;
using System.Runtime.InteropServices;

namespace MusicTagRecover.Win32.FileDialog;

/// <summary>
/// IShellItem interface — wraps native IShellItem for extracting file paths.
/// </summary>
[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler([In, MarshalAs(UnmanagedType.Interface)] object? pbc,
        [In] ref Guid bhid,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    void GetParent([Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

    uint GetDisplayName([In] SIGDN sigdnName,
        [Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

    void GetAttributes([In] uint sfgaoMask,
        [Out] out uint psfgaoAttribs);

    void Compare([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi,
        [In] uint hint,
        [Out] out int piOrder);
}

/// <summary>
/// IShellItemArray interface — wraps native IShellItemArray for enumerating multiple selected items.
/// </summary>
[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    void BindToHandler([In, MarshalAs(UnmanagedType.Interface)] object? pbc,
        [In] ref Guid bhid,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    void GetPropertyStore([In] uint flags,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    void GetPropertyDescriptionList([In, MarshalAs(UnmanagedType.Struct)] ref PROPERTYKEY keyType,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    void GetAttributes([In] uint dwAttribFlags,
        [In] uint sfgaoMask,
        [Out] out uint psfgaoAttribs);

    uint GetCount();

    IShellItem GetItemAt([In] uint dwIndex);

    // Not used but required for vtable layout:
    void EnumItems([Out, MarshalAs(UnmanagedType.Interface)] out object ppenumShellItems);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}
