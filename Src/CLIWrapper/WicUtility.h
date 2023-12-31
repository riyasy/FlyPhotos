#pragma once

#include <atlstr.h>
#include <atlcoll.h>
#include <Wincodec.h>

using namespace std;

#pragma comment(lib, "Windowscodecs.lib")

struct CCodecInfo
{
	CStringW m_strFriendlyName;
	CStringW m_strFileExtensions;
};

class WicUtility
{
public:
	static HRESULT GetWicCodecList(CAtlList<CCodecInfo>& listCodecInfo);
	static bool CopyImagePixelsToMemoryMap(const wchar_t* fileName, const wchar_t* mmfName, bool dest_alpha_needed);

private:
	static HRESULT EnumDecoders(IWICImagingFactory* pImagingFactory,
	                            CAtlList<CCodecInfo>& listCodecInfo);

	static HRESULT EnumEncoders(IWICImagingFactory* pImagingFactory,
	                            CAtlList<CCodecInfo>& listCodecInfo);

	static HRESULT EnumCodecs(IWICImagingFactory* pImagingFactory,
	                          WICComponentType type,
	                          CAtlList<CCodecInfo>& listCodecInfo);
};
