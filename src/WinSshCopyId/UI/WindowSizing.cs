using System.Runtime.InteropServices;

namespace WinSshCopyId.UI;

/// <summary>Native minimum-track sizing avoids recursive Resize calls during layout and DPI changes.</summary>
public sealed class WindowSizing : IDisposable
{
    private readonly IntPtr window;
    private readonly SubclassProc callback;
    private bool installed;
    public WindowSizing(IntPtr window)
    {
        this.window=window;callback=HandleMessage;
        installed=SetWindowSubclass(window,callback,1,0);
        if(!installed)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Minimum window sizing could not be installed.");
    }
    public double Scale {get {var dpi=GetDpiForWindow(window);return dpi==0?1:dpi/96d;}}
    private IntPtr HandleMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,nuint id,nuint reference)
    {
        var result=DefSubclassProc(hwnd,message,wParam,lParam);
        if(message==0x0024&&lParam!=IntPtr.Zero)
        {
            var limits=Marshal.PtrToStructure<MinMaxInfo>(lParam);var scale=Scale;
            limits.MinTrack.X=Math.Max(limits.MinTrack.X,(int)Math.Ceiling(640*scale));
            limits.MinTrack.Y=Math.Max(limits.MinTrack.Y,(int)Math.Ceiling(480*scale));
            Marshal.StructureToPtr(limits,lParam,false);
        }
        return result;
    }
    public void Dispose(){if(installed){RemoveWindowSubclass(window,callback,1);installed=false;}}
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo {public Point Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,nuint id,nuint reference);
    [DllImport("comctl32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd,SubclassProc callback,nuint id,nuint reference);
    [DllImport("comctl32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd,SubclassProc callback,nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
