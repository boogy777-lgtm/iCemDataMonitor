using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Test-only .NET writer. No reference to the proprietary iCem SDK or installed station.
public static class BusProducer {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeMemoryMappedFileHandle CreateFileMappingW(SafeFileHandle file, IntPtr attrs, uint protect, uint high, uint low, string name);
    public static void Run(string path, string mapName, string mutexName, bool smallSection) {
        FileStream file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        MemoryMappedFile mapping = null;
        MemoryMappedViewAccessor view = null;
        SafeMemoryMappedFileHandle small = null;
        Mutex mutex = new Mutex(false, mutexName);
        Thread holder = null;
        ManualResetEvent release = new ManualResetEvent(false);
        try {
            if (smallSection) {
                small = CreateFileMappingW(file.SafeFileHandle, IntPtr.Zero, 4, 0, 4096, mapName);
                if (small.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            } else {
                mapping = MemoryMappedFile.CreateFromFile(file, mapName, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            }
            Console.WriteLine("READY");Console.Out.Flush();
            string line;
            while ((line = Console.ReadLine()) != null) {
                string[] parts = line.Split(' ');
                if (parts[0] == "quit") { Console.WriteLine("BYE");Console.Out.Flush();break; }
                if (parts[0] == "write") {
                    bool held = false;
                    try {
                        try { held = mutex.WaitOne(5000); } catch (AbandonedMutexException) { held = true; }
                        if (!held) throw new TimeoutException();
                        int address = int.Parse(parts[1]);ushort word = ushort.Parse(parts[2]);
                        view.Write(64L + address * 2, (byte)(word >> 8));view.Write(65L + address * 2, (byte)word);
                        // Deliberately no Flush: tests shared mapped-page coherence, not disk polling.
                    } finally { if (held) mutex.ReleaseMutex(); }
                    Console.WriteLine("WRITTEN");
                } else if (parts[0] == "hold") {
                    release.Reset();ManualResetEvent acquired = new ManualResetEvent(false);
                    holder = new Thread(delegate() {
                        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                        acquired.Set();release.WaitOne();mutex.ReleaseMutex();
                    });
                    holder.Start();if (!acquired.WaitOne(5000)) throw new TimeoutException();
                    acquired.Dispose();Console.WriteLine("HELD");
                } else if (parts[0] == "release") {
                    release.Set();holder.Join();holder = null;Console.WriteLine("RELEASED");
                } else if (parts[0] == "abandon") {
                    Thread abandoned = new Thread(delegate() {
                        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                        // Deliberately exit the owning thread without ReleaseMutex.
                    });
                    abandoned.Start();abandoned.Join();Console.WriteLine("ABANDONED");
                } else if (parts[0] == "orphan") {
                    if (view != null) { view.Dispose();view = null; }
                    file.Dispose();file = null;
                    File.Move(path, path + ".gone");Console.WriteLine("MOVED");
                } else { throw new InvalidOperationException("unknown command"); }
                Console.Out.Flush();
            }
        } finally {
            release.Set();if (holder != null) holder.Join();
            if (view != null) view.Dispose();if (mapping != null) mapping.Dispose();
            if (small != null) small.Dispose();if (file != null) file.Dispose();
            mutex.Dispose();release.Dispose();
        }
    }
}
