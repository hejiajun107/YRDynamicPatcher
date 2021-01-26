﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicPatcher
{
	class HookTransferStation : IDisposable
	{
		public LinkedList<HookInfo> HookInfos { get; set; } = new LinkedList<HookInfo>();
		public HookInfo MainHookInfo { get; set; }
		protected byte[] code_over;

		public HookTransferStation(HookInfo info)
		{
			SetHook(info);
		}
		virtual public void SetHook(HookInfo info)
		{
			UnHook();

			HookInfos.AddLast(info);
			MainHookInfo = HookInfos.Max();

			info.TransferStation = this;

			HookAttribute hook = MainHookInfo.GetHookAttribute();
			if (hook.Size > 0)
			{
				code_over = new byte[hook.Size];
				MemoryHelper.Read(hook.Address, code_over, hook.Size);
			}
		}
		// write back origin code
		virtual public void UnHook()
		{
			if (code_over != null)
			{
				HookAttribute hook = MainHookInfo.GetHookAttribute();
				MemoryHelper.Write(hook.Address, code_over, hook.Size);
				ASMWriter.FlushInstructionCache(hook.Address, Math.Max(hook.Size, 5));
				code_over = null;
			}
		}

		// unhook a hook
		virtual public void UnHook(HookInfo info)
		{
			HookInfos.Remove(info);
			// delegate unavailable
			info.CallableDlg = null;
			UnHook();
			MainHookInfo = HookInfos.Max();

			if (HookInfos.Count > 0)
			{
				HookInfos.Remove(MainHookInfo);
				SetHook(MainHookInfo);
			}
		}

		private bool disposedValue;
		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
				}

				UnHook();
				disposedValue = true;
			}
		}
		~HookTransferStation()
		{
			Dispose(disposing: false);
		}
		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}

	class JumpHookTransferStation : HookTransferStation
	{
		public JumpHookTransferStation(HookInfo info) : base(info)
		{
		}

		public override void SetHook(HookInfo info)
		{
			if (HookInfos.Count > 1)
			{
				throw new InvalidOperationException("JumpHook can not mix with other hook");
			}

			base.SetHook(info);

			HookAttribute hook = info.GetHookAttribute();
			switch (hook.Type)
			{
				case HookType.SimpleJumpToRet:
					int address = info.GetReturnValue();
					Logger.Log("jump to address: 0x{0:X}", address);
					ASMWriter.WriteJump(new JumpStruct(hook.Address, address));
					break;
				case HookType.DirectJumpToHook:
					int callable = (int)info.GetCallable();
					Logger.Log("jump to callable: 0x{0:X}", callable);
					ASMWriter.WriteJump(new JumpStruct(hook.Address, callable));
					break;
				default:
					Logger.Log("found unkwnow jump hook: " + info.Method.Name);
					break;
			}
		}
	}
	class AresHookTransferStation : HookTransferStation
	{
		static readonly byte INIT = ASM.INIT;
		static readonly byte[] code_call =
			{
				0x60, 0x9C, //PUSHAD, PUSHFD
		        0x68, INIT, INIT, INIT, INIT, //PUSH HookAddress
		        0x83, 0xEC, 0x04,//SUB ESP, 4
		        0x8D, 0x44, 0x24, 0x04,//LEA EAX,[ESP + 4]
		        0x50, //PUSH EAX
		        0xE8, INIT, INIT, INIT, INIT,  //CALL ProcAddress
		        0x83, 0xC4, 0x0C, //ADD ESP, 0Ch
		        0x89, 0x44, 0x24, 0xF8,//MOV ss:[ESP - 8], EAX
		        0x9D, 0x61, //POPFD, POPAD
		        0x83, 0x7C, 0x24, 0xD4, 0x00,//CMP ss:[ESP - 2Ch], 0
		        0x74, 0x04, //JZ .proceed
		        0xFF, 0x64, 0x24, 0xD4 //JMP ss:[ESP - 2Ch]
            };

		MemoryHandle memoryHandle;

		public AresHookTransferStation(HookInfo info) : base(info)
		{
		}

		IntPtr GetMemory(int size)
		{
			if (memoryHandle == null)
			{
				// alloc bigger space
				memoryHandle = new MemoryHandle(code_call.Length + 0x10 + ASM.Jmp.Length);
			}
			if (memoryHandle.Size < size)
			{
				memoryHandle = new MemoryHandle(size);
			}

			return (IntPtr)memoryHandle.Memory;
		}

		public override void SetHook(HookInfo info)
		{
			base.SetHook(info);

			HookAttribute hook = MainHookInfo.GetHookAttribute();

			var callable = (int)MainHookInfo.GetCallable();
			Logger.Log("ares hook callable: 0x{0:X}", callable);

			int pMemory = (int)GetMemory(code_call.Length + hook.Size + ASM.Jmp.Length);
			Logger.Log("AresHookTransferStation alloc: 0x{0:X}", pMemory);

			if (pMemory != (int)IntPtr.Zero)
			{
				MemoryHelper.Write(pMemory, code_call, code_call.Length);

				MemoryHelper.Write(pMemory + 3, hook.Address);
				ASMWriter.WriteCall(new JumpStruct(pMemory + 0xF, callable));

				var origin_code_offset = pMemory + code_call.Length;

				if (hook.Size > 0)
				{ // write origin code
					MemoryHelper.Write(origin_code_offset, code_over, hook.Size);
					// protected ares hook
					if(code_over[0] == ASM.Jmp[0])
					{
						int destination = 0;
						MemoryHelper.Read(hook.Address + 1, ref destination);
						destination = hook.Address + 5 + destination;

						ASMWriter.WriteJump(new JumpStruct(origin_code_offset, destination));
					}
				}

				var jmp_back_offset = origin_code_offset + hook.Size;
				ASMWriter.WriteJump(new JumpStruct(jmp_back_offset, hook.Address + hook.Size));

				ASMWriter.WriteJump(new JumpStruct(hook.Address, pMemory));

				ASMWriter.FlushInstructionCache(pMemory, memoryHandle.Size);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				memoryHandle.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
