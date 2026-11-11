// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
#ifndef _SINCOS_TABLE_
#define _SINCOS_TABLE_

#include "stdafx.h"
#include <complex>

//******************************************************************************************
template <class T, int BITS_, class CONV=float> class SinCosTable
//
// sin cos table template (length is 1<<BITS)
// create table using "std::polar"
//
{
public:
	enum {
		BITS = BITS_,	// number of bits to index table
		LEN = 1<<BITS,	// number of elements in table
		IMASK = LEN-1	// index mask
	};

	SinCosTable() { m_table = table_(); }
	cplxf operator [] (int i) { return m_table[i]; }

protected:
	T* m_table; // cached table pointer

	static T* table_()
	{
		static bool init_table = true;
		static T table[LEN];
		if(init_table) {
			init_table = false;
			for(long i = 0; i < LEN; i++)
				table[i] = std::polar((CONV)1.0, (CONV)(i*2.0*3.14159265358979323846/LEN));
		}
		return table;
	}
};

#endif
