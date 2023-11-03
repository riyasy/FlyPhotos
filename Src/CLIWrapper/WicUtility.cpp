#include "WicUtility.h"

HRESULT WicUtility::EnumCodecs(IWICImagingFactory* pImagingFactory,
                               const WICComponentType type,
                               CAtlList<CCodecInfo>& listCodecInfo)
{
	ATLASSERT(pImagingFactory);
	ATLASSERT((type == WICDecoder) || (type == WICEncoder));
	listCodecInfo.RemoveAll();

	CComPtr<IEnumUnknown> pEnum;
	const DWORD dwOptions = WICComponentEnumerateDefault;
	const HRESULT hr = pImagingFactory->CreateComponentEnumerator(type,
	                                                              dwOptions, &pEnum);
	if (SUCCEEDED(hr))
	{
		ULONG cbActual = 0;
		CComPtr<IUnknown> pElement = nullptr;
		while (S_OK == pEnum->Next(1, &pElement, &cbActual))
		{
			const UINT cbBuffer = 256;
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

bool WicUtility::CopyImagePixelsToMemoryMap(const wchar_t* fileName, const wchar_t* mmfName, const bool destAlphaNeeded)
{
	CoInitialize(nullptr);
	{
		CComPtr<IWICImagingFactory> pFactory;
		CComPtr<IWICBitmapDecoder> pDecoder;
		CComPtr<IWICBitmapFrameDecode> pFrame;
		CComPtr<IWICFormatConverter> pFormatConverter;

		HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory,
		                              nullptr, CLSCTX_INPROC_SERVER,
		                              IID_PPV_ARGS(&pFactory));

		hr = pFactory->CreateDecoderFromFilename(fileName, nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad,
		                                         &pDecoder);

		UINT frameCount = 0;
		hr = pDecoder->GetFrameCount(&frameCount);

		hr = pDecoder->GetFrame(0, &pFrame);

		UINT width = 0;
		UINT height = 0;
		hr = pFrame->GetSize(&width, &height);

		WICPixelFormatGUID pixelFormatGUID;
		hr = pFrame->GetPixelFormat(&pixelFormatGUID);

		hr = pFactory->CreateFormatConverter(&pFormatConverter);

		hr = pFormatConverter->Initialize(pFrame,
		                                  destAlphaNeeded ? GUID_WICPixelFormat32bppBGRA : GUID_WICPixelFormat24bppRGB,
		                                  WICBitmapDitherTypeNone,
		                                  nullptr,
		                                  0.f,
		                                  WICBitmapPaletteTypeCustom);

		const UINT bytesPerPixel = destAlphaNeeded ? 4 : 3;
		const UINT stride = width * bytesPerPixel;
		const UINT size = width * height * bytesPerPixel;

		const HANDLE FileMappingHandle = CreateFileMapping(
			INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, size, mmfName);
		if (FileMappingHandle == INVALID_HANDLE_VALUE)
		{
			// do error handling
		}
		const auto fileStart = (byte*)MapViewOfFile(FileMappingHandle, FILE_MAP_ALL_ACCESS, 0, 0, size);
		pFormatConverter->CopyPixels(nullptr, stride, size, fileStart);

		if (FileMappingHandle != INVALID_HANDLE_VALUE)
			CloseHandle(FileMappingHandle);
	}
	CoUninitialize();
	return true;
}
