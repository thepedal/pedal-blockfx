// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************

ParamDesc
	Base class for param descriptions


History
	Date       Version    Programmer         Comments
	28/9/03    1.0        Darrell Tam		Created
******************************************************************************/

#ifndef _DT_PARAM_DESC_H_
#define _DT_PARAM_DESC_H_

#include "misc_stuff.h"

class MIBlkFx;

//------------------------------------------------------------------------------------------
class ParamDesc
// interface for param descriptions
{
public:
	// pre & post units
	virtual const char* prefix() const { return ""; }
	virtual const char* postfix() const { return ""; }

	// return text value for v
	virtual void desc(char* txt_out, float/*0..1*/v, MIBlkFx& mi) const = 0;

	// given description text, convert to value (return -1 if can't convert)
	virtual float/*-1=error, 0..1*/param(const char* txt_in, MIBlkFx& mi, int trk_num) const = 0;
};

//------------------------------------------------------------------------------------------
extern ParamDesc* param_desc_hex;
extern ParamDesc* param_desc_percent;
extern ParamDesc* param_desc_percent2;
extern ParamDesc* param_desc_freq_shift;
extern ParamDesc* param_desc_harmonic_width;
extern ParamDesc* param_desc_harmonic_type;

//------------------------------------------------------------------------------------------
inline float/*Hz*/freqShift(float v/*0..1*/)
//
// return frequency as a fraction between -22050 to 22050 Hz
// maps the linear input "v" to an arbitary linear+exponential function
// (linear transitioning into exponential)
//
{
	const float lin = 2048.0f/32768.0f;
	const int exp_raise = 8;
	const float exp_scale = (1.0f-lin)/(1<<exp_raise);
	v = (v-0.5f)*2;

	bool pos = true;
	if(v < 0) {
		v = -v;
		pos = false;
	}
	float r = (powf(2, v*exp_raise)-1.0f)*exp_scale + v*lin;
	return (pos ? r:-r)*22050.0f;
}

//------------------------------------------------------------------------------------------
struct SplitHarmonicParam
{
public:
	// param is split into these
	int/*0..2*/harm_type;
	float/*0..1*/width_frac;

	SplitHarmonicParam(float/*0..1*/param)
	// divide the effect value into 3 segments, with width_frac alternating in direction
	{		
		param *= 3.0f;
		harm_type = limit_range((int)param, 0, 2);
		width_frac = param-harm_type;
		if(! (harm_type&1)) width_frac = 1-width_frac;
	}

	operator float ()
	// combine 2 params, with width frac alternating in direction
	{
		float r = harm_type;
		if(harm_type&1) r += width_frac;
		else r += 1.0f-width_frac;
		return r/3.0f;
	}
};

#endif
