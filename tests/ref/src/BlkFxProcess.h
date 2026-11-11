// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************
DT Block FX - classes to perform actual effects on the FFT'd data

BlkFx
	Base class for effects

BlkFx*
	Individual classes to perform a particular effect on the FFT spectrum

History
	Date       Version    Programmer         Comments
	16/2/03    1.0        Darrell Tam		Created
******************************************************************************/

#ifndef _DT_BLKFX_H_
#define _DT_BLKFX_H_
#include <stdio.h>
#include <vector>
#include "cplx.h"
#include "params.h"
#include "ParamDesc.h"

#include "MIBlkFx.h"

using namespace std;

//------------------------------------------------------------------------------------------
class BlkFxProcess
// interface to FFT processing effects
{
public:
	typedef auto_ptr<BlkFxProcess> AP;

	BlkFxProcess();
	virtual ~BlkFxProcess();

public:
	// number of this effect
	int getEffectNum() { return effect_num; }

	// called from MI_BlockEffects to perform the effect
	virtual void process(); 

protected:
	// process() calls process(pcplxf x,float*) with FFT positions pre-calculated
	virtual void process(pcplxf_range x);

	// match output power to input power 
	void matchPwr(pcplxf_range x, float in_pwr, float out_pwr);

public: int set_index;
protected:	
	// parent machine interface
	MIBlkFx *mi; 

	// effect number
	int effect_num;

public: // factory interface

	// return total number of effects
	static int getTotalNumEffects();

	// 
	static int limitEffectNum(int i) { return limit_range(i, 0, getTotalNumEffects()); }

	// create a new effect instance of type "effect_num"
	static AP create(int effect_num, MIBlkFx* mi);

	// get the name of effect number
	static const char* name(int effect_num);

	void getEffectDesc(int effect_i, int* desc_table_sz, ParamDesc*** desc_table);
};

#endif
