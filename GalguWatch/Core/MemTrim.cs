using System.Runtime.InteropServices;

namespace GalguWatch.Core;

/// <summary>유휴 시점에 워킹셋을 OS에 반납 — DCC 프로그램들이 쓸 RAM을 비워둔다.
/// 필요한 페이지는 다시 쓰일 때 즉시 돌아오므로 체감 비용은 없음.</summary>
public static class MemTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMin, IntPtr dwMax);

    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            SetProcessWorkingSetSize(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }
}
