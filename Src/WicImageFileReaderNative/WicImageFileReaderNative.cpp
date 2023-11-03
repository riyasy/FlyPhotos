#include <string>
#include <iostream>
#include <atlbase.h>
#include <atlstr.h>
#include <atlcoll.h>
#include <Wincodec.h>

using namespace std;

#pragma comment(lib, "Windowscodecs.lib")
bool CopyImagePixelsToMemoryMap(const wstring& filename, const wstring& mmfName, bool destAlphaNeeded);

int wmain(int argc, TCHAR* argv[])
{

	try
	{
		bool destAlphaNeeded = _tcscmp(argv[3], _T("bgra")) == 0;
		if (true == CopyImagePixelsToMemoryMap(argv[1], argv[2], destAlphaNeeded))
			return 0;
		else
			return 1;
	}
	catch (...)
	{
		return 1;
	}
}

bool CopyImagePixelsToMemoryMap(const wstring& filename, const wstring& mmfName, bool destAlphaNeeded)
{
	// initialize COM
	::CoInitialize(nullptr);
	{
		CComPtr<IWICImagingFactory> pFactory;
		CComPtr<IWICBitmapDecoder> pDecoder;
		CComPtr<IWICBitmapFrameDecode> pFrame;
		CComPtr<IWICFormatConverter> pFormatConverter;

		HRESULT hr = ::CoCreateInstance(CLSID_WICImagingFactory,
		                                nullptr, CLSCTX_INPROC_SERVER,
		                                IID_PPV_ARGS(&pFactory));

		hr = pFactory->CreateDecoderFromFilename(filename.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad,
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

		const UINT bytesPerPixel = destAlphaNeeded ? 4 : 3; // Because we have converted the frame to 24-bit RGB
		const UINT stride = width * bytesPerPixel;
		const UINT size = width * height * bytesPerPixel;

		const HANDLE FileMappingHandle = CreateFileMapping(
			INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, size, mmfName.c_str());
		if (FileMappingHandle == INVALID_HANDLE_VALUE)
		{
			// do error handling
		}
		const auto fileStart = (byte*)MapViewOfFile(FileMappingHandle, FILE_MAP_ALL_ACCESS, 0, 0, size);
		pFormatConverter->CopyPixels(nullptr, stride, size, fileStart);

		if (FileMappingHandle != INVALID_HANDLE_VALUE)
			CloseHandle(FileMappingHandle);
	}
	::CoUninitialize();
	return true;
}
