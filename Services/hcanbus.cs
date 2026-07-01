using System;
using System.Runtime.InteropServices;

namespace AudioActuatorCanTest.Services
{
    [Flags]
    public enum CanStatus : int
    {
        STATUS_OK = 0,
        STATUS_ERR = -1,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Can_Config
    {
        public uint Baudrate;
        public short Pres;
        public byte Tseg1;
        public byte Tseg2;
        public byte SJW;
        public byte Config;
        public byte Model;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CanFD_Config
    {
        public uint NomBaud;
        public uint DatBaud;
        public short NomPre;
        public byte NomTseg1;
        public byte NomTseg2;
        public byte NomSJW;
        public byte DatPre;
        public byte DatTseg1;
        public byte DatTseg2;
        public byte DatSJW;
        public byte Config;
        public byte Model;
        public byte Cantype;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Can_Msg
    {
        public uint ID;
        public uint TimeStamp;
        public byte FrameType;
        public byte DataLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] data;

        public byte ExternFlag;
        public byte RemoteFlag;
        public byte BusSatus;
        public byte ErrSatus;
        public byte TECounter;
        public byte RECounter;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CanFD_Msg
    {
        public uint ID;
        public uint TimeStamp;
        public byte FrameType;
        public byte DLC;
        public byte ExternFlag;
        public byte RemoteFlag;
        public byte BusSatus;
        public byte ErrSatus;
        public byte TECounter;
        public byte RECounter;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] data;
    }

    public static class hcanbusdll
    {
        private const string LibraryName = "hcanbus.dll";

        public delegate void HotPlug_Func();

        [DllImport(LibraryName, EntryPoint = "Reg_HotPlug_Func", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int Call_HotPlug_Func(HotPlug_Func pfun);

        [DllImport(LibraryName, EntryPoint = "CAN_ScanDevice", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_ScanDevice();

        [DllImport(LibraryName, EntryPoint = "CAN_OpenDevice", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_OpenDevice(uint devNum);

        [DllImport(LibraryName, EntryPoint = "CAN_CloseDevice", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_CloseDevice(uint devNum);

        [DllImport(LibraryName, EntryPoint = "CAN_Init", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_Init(uint devNum, ref Can_Config initConfig);

        [DllImport(LibraryName, EntryPoint = "CAN_Reset", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_Reset(uint devNum);

        [DllImport(LibraryName, EntryPoint = "CAN_SetFilter", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_SetFilter(
            uint devNum,
            byte namber,
            byte type,
            uint ftID,
            uint ftMask,
            byte enable);

        [DllImport(LibraryName, EntryPoint = "CAN_GetReceiveNum", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_GetReceiveNum(uint devNum);

        [DllImport(LibraryName, EntryPoint = "CAN_Transmit", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_Transmit(
            uint devNum,
            IntPtr send,
            ushort length,
            uint timeout);

        [DllImport(LibraryName, EntryPoint = "CAN_Receive", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CAN_Receive(
            uint devNum,
            IntPtr receive,
            ushort length,
            uint timeout);

        [DllImport(LibraryName, EntryPoint = "CANFD_Init", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CANFD_Init(uint devNum, ref CanFD_Config initConfig);

        [DllImport(LibraryName, EntryPoint = "CANFD_Transmit", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CANFD_Transmit(
            uint devNum,
            IntPtr send,
            ushort length,
            uint timeout);

        [DllImport(LibraryName, EntryPoint = "CANFD_Receive", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int CANFD_Receive(
            uint devNum,
            IntPtr receive,
            ushort length,
            uint timeout);
    }
}

