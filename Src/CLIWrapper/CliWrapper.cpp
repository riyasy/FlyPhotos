#include "ExplorerContextMenu.h"
#include "CliWrapper.h"
#include "marshal.h"


using namespace CliWrapper;

ManagedShellUtility::ManagedShellUtility(): shellUtil(new ShellUtility())
{
}

ManagedShellUtility::~ManagedShellUtility()
{
	delete shellUtil;
	delete wicUtil;
}

void ManagedShellUtility::SayThis(String^ phrase)
{
	shellUtil->SayThis(marshal::to<wchar_t*>(phrase));
}

List<String^>^ ManagedShellUtility::GetFileListFromExplorerWindow()
{
	auto fileListManaged = gcnew List<String^>();
	vector<wstring> fileListNative;
	const HRESULT hr = shellUtil->GetFileListFromExplorerWindow(fileListNative);
	if (SUCCEEDED(hr))
	{
		for (wstring fileNameNativeStr : fileListNative)
		{
			fileListManaged->Add(gcnew String(fileNameNativeStr.c_str()));
		}
	}
	return fileListManaged;
}

bool ManagedShellUtility::ShowContextMenu(String^ fileName, int posX, int posY)
{
	ExplorerContextMenu ctxMenu;
	return ctxMenu.ShowContextMenu(NULL, marshal::to<wchar_t*>(fileName), posX, posY);
}

List<CodecInfo^>^ ManagedShellUtility::GetWicCodecList()
{
	auto listCodecInfoManaged = gcnew List<CodecInfo^>();
	CAtlList<CCodecInfo> listCodecInfoNative;
	const HRESULT hr = wicUtil->GetWicCodecList(listCodecInfoNative);
	if (SUCCEEDED(hr))
	{
		POSITION pos = listCodecInfoNative.GetHeadPosition();
		while (nullptr != pos)
		{
			const CCodecInfo& codecInfoNative = listCodecInfoNative.GetNext(pos);
			std::wstring strTemp1(codecInfoNative.m_strFriendlyName);
			std::wstring strTemp2(codecInfoNative.m_strFileExtensions);

			auto codecInfoManaged = gcnew CodecInfo();
			codecInfoManaged->friendlyName = gcnew String(strTemp1.c_str());
			auto fileExtensions = gcnew String(strTemp2.c_str());
			codecInfoManaged->fileExtensions = gcnew List<String^>();
			cli::array<String^>^ extArray = fileExtensions->Split(',');
			for each (String^ temp in extArray)
			{
				codecInfoManaged->fileExtensions->Add(temp->ToUpperInvariant());
			}
			listCodecInfoManaged->Add(codecInfoManaged);

			// dispay decoder info
			//wprintf_s(L"\nCodec name: %s\nFile extensions: %s\n",
			//    codecInfo.m_strFriendlyName,
			//    codecInfo.m_strFileExtensions);
		}
	}
	return listCodecInfoManaged;
}
