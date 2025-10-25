/**
 * @file WicUtility.h
 * @brief Declares the WicUtility class for enumerating Windows Imaging Component (WIC) codecs.
 *
 * This file defines a utility class and a data structure to retrieve information
 * about the image encoders and decoders registered on the system.
 */

#pragma once

#include <atlstr.h>
#include <atlcoll.h>
#include <Wincodec.h>

using namespace std;

#pragma comment(lib, "Windowscodecs.lib")

/// @brief Holds information about a single WIC codec.
struct CCodecInfo
{
	CStringW m_strFriendlyName;   ///< The user-friendly name of the codec (e.g., "BMP Decoder").
	CStringW m_strFileExtensions; ///< A comma-separated list of file extensions (e.g., ".bmp,.dib").
};

/// @brief Provides static utility methods for interacting with the Windows Imaging Component (WIC).
class WicUtility
{
public:
	/// @brief Populates a list with information about all available WIC image decoders.
	/// @param listCodecInfo A reference to a CAtlList that will be filled with CCodecInfo objects.
	/// @return An HRESULT indicating success (S_OK) or an error code.
	static HRESULT GetWicCodecList(CAtlList<CCodecInfo>& listCodecInfo);

private:
	/// @brief A helper function to enumerate all available WIC decoders.
	/// @param pImagingFactory A pointer to the IWICImagingFactory interface.
	/// @param listCodecInfo A reference to the list to be populated with decoder info.
	/// @return An HRESULT indicating the result of the enumeration.
	static HRESULT EnumDecoders(IWICImagingFactory* pImagingFactory,
		CAtlList<CCodecInfo>& listCodecInfo);

	/// @brief A helper function to enumerate all available WIC encoders.
	/// @param pImagingFactory A pointer to the IWICImagingFactory interface.
	/// @param listCodecInfo A reference to the list to be populated with encoder info.
	/// @return An HRESULT indicating the result of the enumeration.
	static HRESULT EnumEncoders(IWICImagingFactory* pImagingFactory,
		CAtlList<CCodecInfo>& listCodecInfo);

	/// @brief Enumerates WIC components of a specific type (encoder or decoder).
	/// @param pImagingFactory A pointer to the IWICImagingFactory interface.
	/// @param type The type of component to enumerate (WICDecoder or WICEncoder).
	/// @param listCodecInfo A reference to the list to be populated with codec info.
	/// @return An HRESULT indicating the result of the enumeration.
	static HRESULT EnumCodecs(IWICImagingFactory* pImagingFactory,
		WICComponentType type,
		CAtlList<CCodecInfo>& listCodecInfo);
};