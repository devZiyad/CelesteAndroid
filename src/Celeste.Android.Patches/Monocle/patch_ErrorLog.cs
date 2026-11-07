#pragma warning disable CS0626 // extern sem atributo: o MonoMod troca por orig_*
using System;
using CelesteAndroid;
using MonoMod;

namespace Monocle
{
	[MonoModPatch("Monocle.ErrorLog")]
	public static class patch_ErrorLog
	{
		public static extern void orig_Open();

		/// <summary>
		/// O original abre o errorLog.txt com o programa padrão do sistema (Process.Start), o que não existe no Android
		/// (o log continua sendo gravado por ErrorLog.Write). No desktop chama o original sem deixar uma falha
		/// do Process.Start derrubar o tratamento do crash.
		/// </summary>
		public static void Open()
		{
			if (HostConfig.IsAndroid)
			{
				return;
			}
			try
			{
				orig_Open();
			}
			catch (Exception)
			{
			}
		}
	}
}
