#include "WicUtility.h"

HRESULT WicUtility::EnumCodecs(IWICImagingFactory* pImagingFactory,
                               const WICComponentType type,
                               CAtlList<CCodecInfo>& listCodecInfo)
{
	ATLASSERT(pImagingFactory);
	ATLASSERT((type == WICDecoder) || (type == WICEncoder));
	listCodecInfo.RemoveAll();

	CComPtr<IEnumUnknown> pEnum;
	constexpr DWORD dwOptions = WICComponentEnumerateDefault;
	const HRESULT hr = pImagingFactory->CreateComponentEnumerator(type,
	                                                              dwOptions, &pEnum);
	if (SUCCEEDED(hr))
	{
		ULONG cbActual = 0;
		CComPtr<IUnknown> pElement = nullptr;
		while (S_OK == pEnum->Next(1, &pElement, &cbActual))
		{
			constexpr UINT cbBuffer = 256;
			CCodecInfo codecInfo;
			UINT cbActual2 = 0;
			const CComQIPtr<IWICBitmapCodecInfo> pCodecInfo = pElement;
			pCodecInfo->GetFriendlyName(cbBuffer,
			                            codecInfo.m_strFriendlyName.GetBufferSetLength(cbBuffer),
			                            &cbActual2);
			codecInfo.m_strFriendlyName.ReleaseBufferSetLength(cbActual2);
			pCodecInfo->GetFileExtensions(cbBuffer,
			                              codecInfo.m_strFileExtensions.GetBufferSetLength(cbBuffer),
			                              &cbActual2);
			codecInfo.m_strFileExtensions.ReleaseBufferSetLength(cbActual2);
			listCodecInfo.AddTail(codecInfo);
			pElement = nullptr;
		}
	}
	return hr;
}

HRESULT WicUtility::EnumDecoders(IWICImagingFactory* pImagingFactory,
                                 CAtlList<CCodecInfo>& listCodecInfo)
{
	return EnumCodecs(pImagingFactory, WICDecoder, listCodecInfo);
}

HRESULT WicUtility::EnumEncoders(IWICImagingFactory* pImagingFactory,
                                 CAtlList<CCodecInfo>& listCodecInfo)
{
	return EnumCodecs(pImagingFactory, WICEncoder, listCodecInfo);
}

HRESULT WicUtility::GetWicCodecList(CAtlList<CCodecInfo>& listCodecInfo)
{
	CoInitialize(nullptr);
	IWICImagingFactory* pImagingFactory;
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory,
	                              nullptr, CLSCTX_INPROC_SERVER,
	                              IID_PPV_ARGS(&pImagingFactory));
	if (SUCCEEDED(hr))
	{
		hr = EnumDecoders(pImagingFactory, listCodecInfo);
		pImagingFactory->Release();
	}
	CoUninitialize();
	return hr;
}
