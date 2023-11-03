#include "ShellUtility.h"
#include "WicUtility.h"
#include <vector>
#include <string>
#include <windowsx.h>
#include <ole2.h>
#include <shlwapi.h>
#include <shlobj.h>
#include <exdisp.h>
#include <time.h>

using namespace std;

ShellUtility::ShellUtility()
{
}

ShellUtility::~ShellUtility()
{
}

void ShellUtility::SayThis(const wchar_t* phrase)
{
	MessageBox(nullptr, phrase, L"Sample Title", MB_OK);
}

HRESULT ShellUtility::GetFileListFromExplorerWindow(vector<wstring>& arr)
{
	TCHAR g_szItem[MAX_PATH];
	g_szItem[0] = TEXT('\0');

	HWND hwndFind = GetForegroundWindow();

	IShellWindows* psw;
	HRESULT hr;
	hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_IShellWindows, (void**)&psw);
	if (SUCCEEDED(hr))
	{
		VARIANT v;
		V_VT(&v) = VT_I4;
		IDispatch* pdisp;
		BOOL fFound = FALSE;
		for (V_I4(&v) = 0; !fFound && psw->Item(v, &pdisp) == S_OK;
		     V_I4(&v)++)
		{
			IWebBrowserApp* pwba;
			hr = pdisp->QueryInterface(IID_IWebBrowserApp, (void**)&pwba);
			if (SUCCEEDED(hr))
			{
				HWND hwndWBA;
				hr = pwba->get_HWND((LONG_PTR*)&hwndWBA);
				if (SUCCEEDED(hr) && hwndWBA == hwndFind)
				{
					fFound = TRUE;
					IServiceProvider* psp;
					hr = pwba->QueryInterface(IID_IServiceProvider, (void**)&psp);
					if (SUCCEEDED(hr))
					{
						IShellBrowser* psb;
						hr = psp->QueryService(SID_STopLevelBrowser, IID_IShellBrowser, (void**)&psb);
						if (SUCCEEDED(hr))
						{
							IShellView* psv;
							hr = psb->QueryActiveShellView(&psv);
							if (SUCCEEDED(hr))
							{
								IFolderView* pfv;
								hr = psv->QueryInterface(IID_IFolderView, (void**)&pfv);
								if (SUCCEEDED(hr))
								{
									IPersistFolder2* ppf2;
									hr = pfv->GetFolder(IID_IPersistFolder2, (void**)&ppf2);
									if (SUCCEEDED(hr))
									{
										LPITEMIDLIST pidlFolder;
										hr = ppf2->GetCurFolder(&pidlFolder);
										if (SUCCEEDED(hr))
										{
											//if (!SHGetPathFromIDList(pidlFolder, g_szPath)) {
											//    lstrcpyn(g_szPath, TEXT("<not a directory>"), MAX_PATH);
											//}
											int iFocus;
											hr = pfv->GetFocusedItem(&iFocus);
											if (SUCCEEDED(hr))
											{
												LPITEMIDLIST pidlItem;
												hr = pfv->Item(iFocus, &pidlItem);
												if (SUCCEEDED(hr))
												{
													IShellFolder* psf;
													hr = ppf2->QueryInterface(IID_IShellFolder, (void**)&psf);
													if (SUCCEEDED(hr))
													{
														STRRET str;

														IEnumIDList* pEnum;
														hr = pfv->Items(SVGIO_FLAG_VIEWORDER, IID_IEnumIDList,
														                (LPVOID*)&pEnum);
														if (SUCCEEDED(hr))
														{
															LPITEMIDLIST pidl;
															ULONG fetched = 0;

															do
															{
																pidl = nullptr;
																hr = pEnum->Next(1, &pidl, &fetched);
																if (SUCCEEDED(hr))
																{
																	if (fetched)
																	{
																		hr = psf->GetDisplayNameOf(
																			pidl, SHGDN_FORPARSING, &str);
																		if (SUCCEEDED(hr))
																		{
																			StrRetToBuf(&str, pidl, g_szItem, MAX_PATH);
																			arr.push_back(g_szItem);
																		}
																	}
																	CoTaskMemFree(pidl);
																}
															}
															while (fetched);
															pEnum->Release();
														}
														psf->Release();
													}
													CoTaskMemFree(pidlItem);
												}
											}
											CoTaskMemFree(pidlFolder);
										}
										ppf2->Release();
									}
									pfv->Release();
								}
								psv->Release();
							}
							psb->Release();
						}
						psp->Release();
					}
				}
				pwba->Release();
			}
			pdisp->Release();
		}
		psw->Release();
	}
	return hr;
}
