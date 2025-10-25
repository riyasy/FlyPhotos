/**
 * @file WicUtility.cpp
 * @brief Implements the utility functions for enumerating Windows Imaging Component (WIC) codecs.
 */

#include "pch.h"
#include "WicUtility.h"

 /**
  * @brief The core enumeration function that retrieves information for a specific type of WIC component.
  * @param pImagingFactory A pointer to an initialized IWICImagingFactory.
  * @param type The type of component to enumerate (WICDecoder or WICEncoder).
  * @param listCodecInfo A reference to a CAtlList to be populated with the results.
  * @return An HRESULT indicating success or failure.
  */
HRESULT WicUtility::EnumCodecs(IWICImagingFactory* pImagingFactory,
	const WICComponentType type,
	CAtlList<CCodecInfo>& listCodecInfo)
{
	// Assert that preconditions are met.
	ATLASSERT(pImagingFactory);
	ATLASSERT((type == WICDecoder) || (type == WICEncoder));
	listCodecInfo.RemoveAll(); // Clear any existing data in the list.

	// Create an enumerator for the specified component type.
	CComPtr<IEnumUnknown> pEnum;
	constexpr DWORD dwOptions = WICComponentEnumerateDefault;
	HRESULT hr = pImagingFactory->CreateComponentEnumerator(type, dwOptions, &pEnum);

	if (SUCCEEDED(hr))
	{
		ULONG cbActual = 0;
		CComPtr<IUnknown> pElement; // CComPtr for the generic component.

		// Loop through all the components found by the enumerator.
		// CComPtr's operator& is designed for this, it will manage the pointer correctly.
		while (S_OK == pEnum->Next(1, &pElement, &cbActual))
		{
			// --- FIX IS HERE ---
			// Each component is an IUnknown; we need to query for the specific info interface.
			// Instead of CComQIPtr, use a CComPtr and do the QI manually.
			CComPtr<IWICBitmapCodecInfo> pCodecInfo;
			hr = pElement->QueryInterface(IID_PPV_ARGS(&pCodecInfo));

			if (SUCCEEDED(hr) && pCodecInfo) // Check that QI succeeded and pointer is not null
			{
				constexpr UINT cbBuffer = 256; // Define a reasonable buffer size for strings.
				CCodecInfo codecInfo;
				UINT cbActual2 = 0;

				// Retrieve the codec's friendly name.
				// Use the new pCodecInfo pointer
				pCodecInfo->GetFriendlyName(cbBuffer,
					codecInfo.m_strFriendlyName.GetBufferSetLength(cbBuffer),
					&cbActual2);
				codecInfo.m_strFriendlyName.ReleaseBufferSetLength(cbActual2);

				// Retrieve the codec's associated file extensions.
				pCodecInfo->GetFileExtensions(cbBuffer,
					codecInfo.m_strFileExtensions.GetBufferSetLength(cbBuffer),
					&cbActual2);
				codecInfo.m_strFileExtensions.ReleaseBufferSetLength(cbActual2);

				// Add the retrieved information to our output list.
				listCodecInfo.AddTail(codecInfo);
			}

			// Reset the pElement smart pointer for the next loop iteration.
			// This releases the current IUnknown and prepares the CComPtr for the next call to Next().
			pElement.Release();
		}
	}
	return hr;
}

/**
 * @brief A convenience wrapper to enumerate only decoders.
 */
HRESULT WicUtility::EnumDecoders(IWICImagingFactory* pImagingFactory,
	CAtlList<CCodecInfo>& listCodecInfo)
{
	return EnumCodecs(pImagingFactory, WICDecoder, listCodecInfo);
}

/**
 * @brief A convenience wrapper to enumerate only encoders.
 */
HRESULT WicUtility::EnumEncoders(IWICImagingFactory* pImagingFactory,
	CAtlList<CCodecInfo>& listCodecInfo)
{
	return EnumCodecs(pImagingFactory, WICEncoder, listCodecInfo);
}

/**
 * @brief A simple RAII (Resource Acquisition Is Initialization) helper for COM initialization.
 * @details The constructor calls CoInitialize, and the destructor calls CoUninitialize,
 *          ensuring that COM is properly cleaned up even if errors occur.
 */
struct CoInitializer {
	HRESULT hr;
	CoInitializer() { hr = CoInitialize(nullptr); }
	~CoInitializer() { if (SUCCEEDED(hr)) CoUninitialize(); }
};

/**
 * @brief The main public function to get the list of WIC decoders.
 * @details This function handles the entire process: COM initialization, creating the
 *          WIC factory, calling the enumeration function, and ensuring COM is uninitialized.
 */
HRESULT WicUtility::GetWicCodecList(CAtlList<CCodecInfo>& listCodecInfo)
{
	// This will now automatically call CoInitialize and guarantee CoUninitialize is called
	// when the function exits, for any reason.
	CoInitializer coInit;
	if (FAILED(coInit.hr))
	{
		// If COM initialization fails, we cannot proceed.
		return coInit.hr;
	}

	// Use CComPtr for automatic resource management of the COM interface pointer.
	CComPtr<IWICImagingFactory> pImagingFactory;
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory,
		nullptr, CLSCTX_INPROC_SERVER,
		IID_PPV_ARGS(&pImagingFactory));

	if (SUCCEEDED(hr))
	{
		// If the factory was created successfully, enumerate the decoders.
		// No need to manually release pImagingFactory, CComPtr handles it.
		hr = EnumDecoders(pImagingFactory, listCodecInfo);
	}

	return hr;
}