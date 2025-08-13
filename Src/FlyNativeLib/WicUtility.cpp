#include "pch.h"
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
	HRESULT hr = pImagingFactory->CreateComponentEnumerator(type, dwOptions, &pEnum);

	if (SUCCEEDED(hr))
	{
		ULONG cbActual = 0;
		CComPtr<IUnknown> pElement; // No need to initialize to nullptr, default ctor does it

		// CComPtr's operator& is designed for this, it will manage the pointer correctly.
		while (S_OK == pEnum->Next(1, &pElement, &cbActual))
		{
			// --- FIX IS HERE ---
			// Instead of CComQIPtr, use a CComPtr and do the QI manually.
			CComPtr<IWICBitmapCodecInfo> pCodecInfo;
			hr = pElement->QueryInterface(IID_PPV_ARGS(&pCodecInfo));

			if (SUCCEEDED(hr) && pCodecInfo) // Check that QI succeeded and pointer is not null
			{
				constexpr UINT cbBuffer = 256;
				CCodecInfo codecInfo;
				UINT cbActual2 = 0;

				// Use the new pCodecInfo pointer
				pCodecInfo->GetFriendlyName(cbBuffer,
					codecInfo.m_strFriendlyName.GetBufferSetLength(cbBuffer),
					&cbActual2);
				codecInfo.m_strFriendlyName.ReleaseBufferSetLength(cbActual2);

				pCodecInfo->GetFileExtensions(cbBuffer,
					codecInfo.m_strFileExtensions.GetBufferSetLength(cbBuffer),
					&cbActual2);
				codecInfo.m_strFileExtensions.ReleaseBufferSetLength(cbActual2);

				listCodecInfo.AddTail(codecInfo);
			}

			// Reset the pElement smart pointer for the next loop iteration.
			// This releases the current IUnknown and prepares the CComPtr for the next call to Next().
			pElement.Release();
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

// Add this simple RAII helper to a utility header or at the top of the cpp file
struct CoInitializer {
	HRESULT hr;
	CoInitializer() { hr = CoInitialize(nullptr); }
	~CoInitializer() { if (SUCCEEDED(hr)) CoUninitialize(); }
};


HRESULT WicUtility::GetWicCodecList(CAtlList<CCodecInfo>& listCodecInfo)
{
	// This will now automatically call CoInitialize and guarantee CoUninitialize is called
	// when the function exits, for any reason.
	CoInitializer coInit;
	if (FAILED(coInit.hr))
	{
		return coInit.hr;
	}

	CComPtr<IWICImagingFactory> pImagingFactory; // Use CComPtr for automatic release
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory,
		nullptr, CLSCTX_INPROC_SERVER,
		IID_PPV_ARGS(&pImagingFactory));
	if (SUCCEEDED(hr))
	{
		// No need to manually release pImagingFactory, CComPtr handles it.
		hr = EnumDecoders(pImagingFactory, listCodecInfo);
	}

	return hr;
}
