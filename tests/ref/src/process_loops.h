// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************



******************************************************************************/

#ifndef _PROCESS_LOOPS_H_
#define _PROCESS_LOOPS_H_

//------------------------------------------------------------------------------------------
template <class I> inline void rampCopy(I dst, I src, float scale, float step, long n)
// ramp src into dst
{
	long i;
	for(i = 0; i < n; i++)
	{
		*dst = (*src)*scale;
		src++;
		dst++;
		scale += step;
	}
}

//------------------------------------------------------------------------------------------
template <class I> inline void rampMix1(I dst, I src, float src_scale,
										float dst_ramp, float dst_ramp_step, long n)
// scale src, ramp dst, mix with src and put back into dst
{
	long i;
	for(i = 0; i < n; i++)
	{
		*dst = (*src)*src_scale+(*dst)*dst_ramp;
		src++;
		dst++;
		dst_ramp += dst_ramp_step;
	}
}

#endif
