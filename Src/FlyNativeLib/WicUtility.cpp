/**
 * @file WicUtility.cpp
 * @brief Implements the utility functions for enumerating Windows Imaging Component (WIC) codecs.
 */

#include "pch.h"
#include "WicUtility.h"
#include <vector>
#include <future>

/**
 * @brief The core enumeration function that retrieves information for a specific type of WIC component.
 *
 * @details
 * Performance strategy (replaces the original serial CoCreateInstance-per-codec approach):
 *
 *  Phase 1 — Whitelist built-in codecs.
 *    Re-enumerate with WICComponentEnumerateBuiltInOnly to obtain the set of CLSIDs that
 *    ship with Windows (BMP, GIF, JPEG, PNG, TIFF, DDS, WMPhoto, DNG, HEIF, WebP, Raw, etc.).
 *    These are guaranteed to be instantiable — no CoCreateInstance check needed.
 *
 *  Phase 2 — Enumerate all codecs and classify.
 *    Built-in match  → add to list immediately (zero CoCreateInstance overhead).
 *    Non-built-in    → if bProbeNonBuiltIn is true, launch a thread-pool task
 *                      via std::async to test CoCreateInstance concurrently.
 *                      If false (default), add non-built-ins directly without validation.
 *
 *  Phase 3 — Collect parallel results and add passing non-built-ins (only when probing).
 */
HRESULT WicUtility::EnumCodecs(IWICImagingFactory* pImagingFactory,
	const WICComponentType type,
	CAtlList<CCodecInfo>& listCodecInfo,
	bool bProbeNonBuiltIn /*= false*/)
{
	ATLASSERT(pImagingFactory);
	ATLASSERT((type == WICDecoder) || (type == WICEncoder));
	listCodecInfo.RemoveAll();

	// -------------------------------------------------------------------------
	// Phase 1: Collect CLSIDs of built-in codecs.
	// -------------------------------------------------------------------------
	std::vector<CLSID> builtInClsids;
	{
		CComPtr<IEnumUnknown> pBuiltInEnum;
		if (SUCCEEDED(pImagingFactory->CreateComponentEnumerator(
			type, WICComponentEnumerateBuiltInOnly, &pBuiltInEnum)))
		{
			ULONG nFetched = 0;
			CComPtr<IUnknown> pBI;
			while (S_OK == pBuiltInEnum->Next(1, &pBI, &nFetched))
			{
				CComPtr<IWICBitmapCodecInfo> pBIInfo;
				if (SUCCEEDED(pBI->QueryInterface(IID_PPV_ARGS(&pBIInfo))))
				{
					CLSID biClsid;
					if (SUCCEEDED(pBIInfo->GetCLSID(&biClsid)))
						builtInClsids.push_back(biClsid);
				}
				pBI.Release();
			}
		}
	}

	auto IsBuiltIn = [&builtInClsids](const CLSID& clsid) -> bool
	{
		for (const CLSID& bi : builtInClsids)
			if (IsEqualCLSID(bi, clsid)) return true;
		return false;
	};

	// Helper: extract CCodecInfo strings from IWICBitmapCodecInfo.
	auto ExtractCodecInfo = [](IWICBitmapCodecInfo* pInfo) -> CCodecInfo
	{
		CCodecInfo ci;
		UINT cb = 0;

		pInfo->GetFriendlyName(0, nullptr, &cb);
		if (cb > 0)
		{
			pInfo->GetFriendlyName(cb, ci.m_strFriendlyName.GetBufferSetLength(cb), &cb);
			ci.m_strFriendlyName.ReleaseBuffer();
		}

		cb = 0;
		pInfo->GetFileExtensions(0, nullptr, &cb);
		if (cb > 0)
		{
			pInfo->GetFileExtensions(cb, ci.m_strFileExtensions.GetBufferSetLength(cb), &cb);
			ci.m_strFileExtensions.ReleaseBuffer();
		}
		return ci;
	};

	// -------------------------------------------------------------------------
	// Phase 2: Enumerate all codecs. Built-ins are added immediately;
	//          non-built-ins get a concurrent CoCreateInstance probe.
	// -------------------------------------------------------------------------
	CComPtr<IEnumUnknown> pEnum;
	HRESULT hr = pImagingFactory->CreateComponentEnumerator(
		type, WICComponentEnumerateDefault, &pEnum);
	if (FAILED(hr))
		return hr;

	struct PendingCodec
	{
		CCodecInfo           info;
		std::future<HRESULT> testResult;
	};
	std::vector<PendingCodec> pending;

	ULONG cbActual = 0;
	CComPtr<IUnknown> pElement;
	while (S_OK == pEnum->Next(1, &pElement, &cbActual))
	{
		CComPtr<IWICBitmapCodecInfo> pCodecInfo;
		if (SUCCEEDED(pElement->QueryInterface(IID_PPV_ARGS(&pCodecInfo))) && pCodecInfo)
		{
			CLSID clsid;
			if (SUCCEEDED(pCodecInfo->GetCLSID(&clsid)))
			{
				if (IsBuiltIn(clsid))
				{
					// Built-in: guaranteed valid — add directly, no test needed.
					listCodecInfo.AddTail(ExtractCodecInfo(pCodecInfo));
				}
				else
				{
					if (!bProbeNonBuiltIn)
					{
						// Probe skipped — caller accepts all non-built-in codecs as-is.
						listCodecInfo.AddTail(ExtractCodecInfo(pCodecInfo));
					}
					else
					{
						// Non-built-in (Store extension, third-party):
						// Probe CoCreateInstance on a thread-pool thread so all probes
						// run concurrently instead of serially.
						PendingCodec pc;
						pc.info = ExtractCodecInfo(pCodecInfo);
						pc.testResult = std::async(std::launch::async, [clsid]() -> HRESULT
							{
								// Thread-pool threads need their own COM apartment.
								HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
								HRESULT hrInst = E_FAIL;

								{ // Scope so CComPtr releases before CoUninitialize
									CComPtr<IUnknown> pTest;
									hrInst = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&pTest));
								}

								if (SUCCEEDED(hrCom))
									CoUninitialize();

								return hrInst;
							});
						pending.push_back(std::move(pc));
					}
				}
			}
		}
		pElement.Release();
	}

	// -------------------------------------------------------------------------
	// Phase 3: Collect concurrent results.
	// By the time the main loop finishes, most async tasks are already done.
	// -------------------------------------------------------------------------
	for (auto& pc : pending)
	{
		if (SUCCEEDED(pc.testResult.get()))
			listCodecInfo.AddTail(pc.info);
	}

	return S_OK;
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