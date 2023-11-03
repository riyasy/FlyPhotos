#pragma once

namespace marshal
{
	template <typename T>

	static T to(String^ str)
	{
	}

	template <>

	static wchar_t* to(String^ str)
	{
		pin_ptr<const wchar_t> cpwc = PtrToStringChars(str);
		const int len = str->Length + 1;
		const auto pwc = new wchar_t[len];
		wcscpy_s(pwc, len, cpwc);
		return pwc;
	}
}
