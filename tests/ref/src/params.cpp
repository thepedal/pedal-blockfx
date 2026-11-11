// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/*
 * Buzz parameter interface, see params.h for more information
 *
 * History
 * Date       Version    Programmer         Comments
 * 16/2/03    1.0        Darrell Tam		Created
 */

#include "stdafx.h"
#include "ParamDesc.h"
#include "BlkFxProcess.h"
#include "MIBlkFx.h"
#include "params.h"

using namespace std;

//------------------------------------------------------------------------------------------
// any descriptions which call MIBlkFx are here



#define PARAM(CLASS, NAME) NAME.update(b.NAME);
//------------------------------------------------------------------------------------------
void Params::TVals::update(const TVals& b)
{
	TRK_PARAMS
}

//------------------------------------------------------------------------------------------
void Params::update(const Params& b)
{
	tv_n = b.tv_n;
	GLOBAL_PARAMS
	for(int i = 0; i < tv_n; i++) tv[i].update(b.tv[i]);
}
#undef PARAM

//------------------------------------------------------------------------------------------
ParamsVec::ParamsVec()
{
	v.resize(128); // arbitary initial size
	reset();
}

//------------------------------------------------------------------------------------------
void ParamsVec::reset(void)
{
	in = &v[0];
	in->copy(Params());
	in->pos_abs = 0;
	in->tv_n = 0;
	in->samps_per_tick = -1; // tag as initialParam

	curr = &v[0];
	next = curr;
}


//------------------------------------------------------------------------------------------
void ParamsVec::incOutPos(void)
// go to the next output
{
	if(++curr == (&v[0]+v.size())) curr = &v[0]; // wrap

	next = curr+1;
	if(next == (&v[0]+v.size())) next = &v[0]; // wrap

	filled_prev = filled_curr;
	filled_curr.update(*curr);
	tick_frac = 0.0f;
}


