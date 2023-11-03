#pragma once

#include "ShellUtility.h"
#include "WicUtility.h"

using namespace System::Collections::Generic;
using namespace System;

namespace CliWrapper
{
	public ref class CodecInfo
	{
	public:
		String^ friendlyName;
		List<String^>^ fileExtensions;
	};

	public ref class ManagedShellUtility
	{
	private:
		ShellUtility* shellUtil;
		WicUtility* wicUtil;

	public:
		ManagedShellUtility();
		~ManagedShellUtility();
		void SayThis(String^ phrase);
		List<String^>^ GetFileListFromExplorerWindow();
		List<CodecInfo^>^ GetWicCodecList();
		bool CopyImagePixelsToMemoryMap(String^ fileName, String^ mmfName, bool destAlphaNeeded);
	};
}
