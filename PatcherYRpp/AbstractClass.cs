﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PatcherYRpp
{

	[StructLayout(LayoutKind.Explicit, Size = 36)]
	public struct AbstractClass
	{
		[FieldOffset(0)]
		public int Vfptr;
	}
	[StructLayout(LayoutKind.Explicit, Size = 152, CharSet = CharSet.Ansi, Pack = 1)]
	public struct AbstractTypeClass
	{
		//[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x18)]
		//[FieldOffset(36)] public string ID;

		// offset 60 is ok, but it is 61
		//[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x20)]
		//[FieldOffset(61)] public string UINameLabel;

		//[MarshalAs(UnmanagedType.LPWStr)]
		//[FieldOffset(96)] public string UIName;

		//[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x31)]
		//[FieldOffset(100)] public string Name;

		[FieldOffset(96)]
		public unsafe IntPtr UIName;
	}
}
