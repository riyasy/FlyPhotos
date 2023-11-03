#pragma once

#include <Windows.h>
#include <vector>
#include <string>

using namespace std;

class __declspec(dllexport) ShellUtility
{
public:
	ShellUtility();
	~ShellUtility();

	static void SayThis(const wchar_t* phrase);
	static HRESULT GetFileListFromExplorerWindow(vector<wstring>& arr);
};
