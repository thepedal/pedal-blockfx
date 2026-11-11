// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************
Buzz parameter interface


GLOBAL_PARAMS
	Macro holding mapping between param structure and param name for effect
	"global" (to all track) effects
  
TRK_PARAMS
	Similar to "GLOBAL_PARAMS" for track params

Params
	Structure generated from GLOBAL_PARAMS that is stored inside of the machine
	interface class and is directly updated by Buzz. Also used in ParamsVec.

Params::TVals
    Generated from TRK_PARAMS for per-track values.

ParamsVec
	Vector of GVals used for storing previous GVals so that GVals corresponding
	to delayed audio data can be retrieved. Also performs linear interpolation of
	params when requested sample position falls between ticks.

	Behaves like a FIFO.

History
	Date       Version    Programmer         Comments
	16/2/03    1.0        Darrell Tam		 Created
	14/2/04    1.1        DT                 Changed to template based
******************************************************************************/

#ifndef _DTBLOCKTRK_PARAMS_H_
#define _DTBLOCKTRK_PARAMS_H_

#include <string>
#include <vector>
#include <iostream>
#include <fstream>
#include <MachineInterface.h>

#include "FreqToNote.h"
#include "misc_stuff.h"
using namespace std;


// number of effect sets
#ifdef ALL_PARAMS_GLOBAL
// if ALL_PARAMS_GLOBAL is set there are no track params
// params that are normally track params are generated as global params
// which is useful for "FruityLoops"
const long N_FX_PARAM_SETS = 4;
#else
const long N_FX_PARAM_SETS = 16;
#endif

// number of FFT block sizes (used when creating FFT plans)
#define N_FFT_BLK_SZ 12

// smallest block size, block size of 0 corresponds to this value
const long BLK_SZ_0 = 32;
#define GET_BLK_SZ(PLAN_N) (BLK_SZ_0<<(PLAN_N))

// largest block size
const long max_blk_sz = BLK_SZ_0 << (N_FFT_BLK_SZ-1);

// internal output delay buffer length
const long x3_len = 44100*6;

// maximum samples delay that we can do
const long max_x3_delay = x3_len-max_blk_sz-256;

class MIBlkFx;

#pragma pack(1)

//------------------------------------------------------------------------------------------
template <class DATA, int BUZZ_TYPE_, int FLAGS_, int MIN_VAL_, int MAX_VAL_, int NO_VAL_, int DEF_VAL_>
struct ParamT
//
// every buzz param is wrapped in this class
//
{
	typedef ParamT base;

	enum { BUZZ_TYPE=BUZZ_TYPE_, FLAGS=FLAGS_, MIN_VAL=MIN_VAL_, MAX_VAL=MAX_VAL_, NO_VAL=NO_VAL_, DEF_VAL=DEF_VAL_};
	DATA data;
	ParamT(float v = DEF_VAL) { data = v; }

	static float limit(float v) { return limit_range(v, MIN_VAL, MAX_VAL); }
	static int limit(int v) { return limit_range(v, MIN_VAL, MAX_VAL); }

	// force set
	void set(int v) { data = v; }
	int get() const { return data; }
	void limitSet(float v) { set(limit(v+0.5f)); }
	void limitSet(int v) { set(limit(v)); }
	int limitGet() const { return limit(get()); }

	// fraction between 0 & 1 inclusive
	float getFrac() const { return (float)(limitGet()-MIN_VAL)/(MAX_VAL-MIN_VAL); } \
	void setFrac(float f) { set(f*(MAX_VAL-MIN_VAL)+MIN_VAL+0.5f); }

	// has value?
	bool isNoVal() const { return data == NO_VAL; }
	void setNoVal() { data = NO_VAL; }
	void update(int b) { if(b != NO_VAL) set(b); }

	void operator = (int b) { set(b); }

	// auto cast to int
	operator int () const { return data; }

	// print range message
	static void rngMsg(ostream& o)
	{ o<<" (range=0x"<<hex<<MIN_VAL<<"..0x"<<MAX_VAL<<", default=0x"<<DEF_VAL<< ")"; }

};

//------------------------------------------------------------------------------------------
struct _AmpParam : public ParamT<byte, pt_byte, MPF_STATE, 0, 0xE0, 0xFF, 0xA0>
{
	enum { ZERO_dB = 0xA0, UNITS_PER_dB = 2 };
	void setMult(float m) { limitSet(m>0?log10f(m)*20.0f*UNITS_PER_dB+ZERO_dB:0); }
	static float getMult(float v) { return v>0?powf(10, (limit(v)-ZERO_dB)/20.0f/UNITS_PER_dB):0.0f; }
	float getMult() const { return getMult(get()); }

	void setdB(float v) { limitSet(v*UNITS_PER_dB+ZERO_dB); }
	static float getdB(float v) { return (limit(v)-ZERO_dB)/UNITS_PER_dB; }
	float getdB() const { return getdB(get()); }

	static void rngMsg(ostream& o)
	{  o<<" (0x"<<hex<<MIN_VAL<<"=-inf dB (min)"
		<<", 0x"<<hex<<DEF_VAL<<"="<<dec<<getdB(DEF_VAL)<<"dB (default)"
		<<", 0x"<<hex<<MAX_VAL<<"="<<dec<<getdB(MAX_VAL)<<"dB (max)"
		<<", units="<<1.0f/UNITS_PER_dB<<"dB)"; }

	void desc(char* txt, MIBlkFx& i) const
	{ sprintf(txt, data>0?"%.1fdB":"-inf dB", getdB()); }
};

//------------------------------------------------------------------------------------------
struct _FreqParam : public ParamT<word, pt_word, MPF_STATE, 0, 132*256, 0xffff, 0>
{
	// old version: return powf(2.0f, lin_interp((float)v/param_frac_max, -10.2f, 0));
	// new version, align hex values to notes starting at c0
	enum { BUZZ_OCTAVE=256*12 }; // buzz values per octave
	static float getHz(float v) { return v>0?A4_FREQ*powf(2.0f, limit(v)/BUZZ_OCTAVE-(4.0f+9.0f/12.0f)):0.0f; }
	float getHz() const { return getHz(get()); }
	void setHz(float hz) { limitSet(hz>0?(logf(hz/A4_FREQ)/logf(2.0f)+(4.0f+9.0f/12.0f))*BUZZ_OCTAVE:0); }
	void desc(char* txt, MIBlkFx& i) const;
	static void rngMsg(ostream& o)
	{ o << " c1=0c00 c2=1800 c3=2400 c4=3000 c5=3c00 c6=4800 c7=5400, units=1/0x100 note"; base::rngMsg(o); }
};

//------------------------------------------------------------------------------------------
struct BlkSyncParam : public ParamT<byte, pt_switch, /*flags*/0, SWITCH_OFF, SWITCH_ON, SWITCH_NO, SWITCH_OFF>
{
	static const char* name() { return "BlkSync"; }
	static void help(ostringstream s) { s<<"Synchronize block with this tick"; }
	void desc(char* txt, MIBlkFx& i) { strcpy(txt, get()?"Block sync":"Free run"); }
};
struct InterpModeParam : public ParamT<byte, pt_byte, /*flags*/0, 0, 2, 0xff, 0>
{
	static const char* name() { return "InterpMode"; }
	static void help(ostringstream s) { s<<"Parameter Interpolation Mode (0=normal, 1=off, 2=continue previous)"; }
	void desc(char* txt, MIBlkFx& i) const
	{
		static const char* t[] = {"Normal", "Off", "Continue Previous"};
		strcpy(txt, t[limitGet()]);
	}
};
struct MixBackParam : public ParamT<byte, pt_byte, MPF_STATE, 0, 0xA0, 0xff, 0>
{
	static const char* name() { return "MixBack"; }
	static void help(ostringstream s) { s<<"Percentage mix back of original audio (0=0%, 0xA0=100%)"; }
	void desc(char* txt, MIBlkFx& i) const
	{ sprintf(txt, "%.1f%%", getFrac()*100.0f); }
};
struct OutAmpParam : public _AmpParam
{
	static const char* name() { return "OutAmp"; }
	static void help(ostringstream s) { s<<"Output amplification"; rngMsg(s); }
};
struct TickDelayParam : public ParamT<word, pt_word, MPF_STATE, 0, 0x4000, 0xffff, 0x400>
{
	enum { TICK = 0x100 }; // one tick

	static const char* name() { return "TickDelay"; }
	static void help(ostringstream s) { s<<"Audio is delayed for this many ticks, units=1/0x"<<hex<<TICK<<" ticks"; rngMsg(s); }
	void desc(char* txt, MIBlkFx& i) const { sprintf(txt, "%.2f", (float)limitGet()/TICK); }
	static float getSamples(float v, long samps_per_tick) { return limit_range(v/TICK*samps_per_tick, BLK_SZ_0, max_x3_delay); }
	float getSamples(long samps_per_tick) const { return getSamples(get(), samps_per_tick); }
	void setSamples(float samps, long samps_per_tick) { limitSet(samps*TICK/samps_per_tick); }
};
struct BlkLenParam : public ParamT<byte, pt_byte, MPF_STATE, 0, 11, 0xff, 7>
{
	static const char* name() { return "BlockLen"; }
	static void help(ostringstream s) { s<<"Audio block length"
		<<", 0x0="<<dec<<GET_BLK_SZ(0)<<"(min)"
		<<", 0x1="<<dec<<GET_BLK_SZ(1)
		<<", 0x2="<<dec<<GET_BLK_SZ(2)
		<<", 0x"<<hex<<DEF_VAL<<"="<<dec<<GET_BLK_SZ(DEF_VAL)<<"(default)"
		<<", 0x"<<hex<<MAX_VAL<<"="<<dec<<GET_BLK_SZ(MAX_VAL)<<"(max) samples"; }
	void desc(char* txt, MIBlkFx& i) const;
};
struct OverlapParam : public ParamT<byte, pt_byte, MPF_STATE, 5, 80, 0xff, 50>
{
	static const char* name() { return "Overlap"; }
	static void help(ostringstream s) { s<<"Percentage overlap of blocks "; rngMsg(s); }
	static float getOverlapFrac(float v) { return limit(v)/100.0f; }

	float getOverlapFrac() const { return getOverlapFrac(get()); }
	void setOverlapFrac(float f) { set(f*100); }
	void desc(char* txt, MIBlkFx& i) const { sprintf(txt, "%d%%", limitGet()); }
};
struct FreqATrkParam : public _FreqParam
{
	static const char* name() { return "FreqA"; }
	static void help(ostringstream s) { s<<"Frequency A"; rngMsg(s); }
};
struct FreqBTrkParam : public _FreqParam
{
	static const char* name() { return "FreqB"; }
	static void help(ostringstream s) { s<<"Frequency B"; rngMsg(s); }
};
struct AmpTrkParam : public _AmpParam
{
	static const char* name() { return "Amp"; }
	static void help(ostringstream s) { s<<"Amplitude"; rngMsg(s); }
};
struct EffectTrkParam : public ParamT<byte, pt_byte, MPF_STATE, 0, 8, 0xff, 0>
{	
	static const char* name() { return "Effect"; }
	static void help(ostringstream s) { s<<"Effect (0=contrast, 1=smear, 2=clip, 3=weed, 4=shift add, 5=shift replace, 6=harmonic, 7=auto harm, 8=copy harm)"; }
	void desc(char* txt, MIBlkFx& i) const;
};
struct FxValTrkParam : public ParamT<word, pt_word, MPF_STATE, 0, 0xa000, 0xffff, 0>
{
	static const char* name() { return "Value"; }
	static void help(ostringstream s) { s<<"Value"; rngMsg(s); }
	void desc(char* txt, MIBlkFx& i) const
	{
		// unfortunately we can't tell what the effect number is to use a better description
		sprintf(txt, "%.3f", getFrac());
	}
};

//------------------------------------------------------------------------------------------
// macros used for building structures
//

#define GLOBAL_PARAMS \
	PARAM(BlkSyncParam, blk_sync) \
	PARAM(InterpModeParam, interp_mode) \
	PARAM(MixBackParam, mix_back) \
	PARAM(OutAmpParam, out_amp) \
	PARAM(TickDelayParam, tick_delay) \
	PARAM(BlkLenParam, blk_len) \
	PARAM(OverlapParam, overlap)

#define TRK_PARAMS \
	PARAM(FreqATrkParam, freq_a) \
	PARAM(FreqBTrkParam, freq_b) \
	PARAM(AmpTrkParam, amp) \
	PARAM(EffectTrkParam, effect) \
	PARAM(FxValTrkParam, fxval)

//------------------------------------------------------------------------------------------
struct Params
//
// Params structure that buzz accesses
//
{
	#define PARAM(CLASS, NAME) CLASS NAME;
	GLOBAL_PARAMS
	struct TVals {
		TRK_PARAMS
		void update(const TVals& b);
	} tv[N_FX_PARAM_SETS];
	#undef PARAM

	int tv_n;	// number of valid track vals

public: //
	Params() { tv_n = 0; }

	// update copies params from "b" that are not "NO_VAL"
	void update(const Params& b);

	// verbatim copy
	void copy(const Params& p) { memcpy(this, &p, sizeof(p)); }
};
#pragma pack()


//------------------------------------------------------------------------------------------
// stream output of Params for debugging
#define PARAM(CLASS, NAME) o << ", " #NAME "=" << hex << (int)p.NAME.data;
inline ostream& operator << (ostream& o, const Params::TVals& p) { TRK_PARAMS return o; }
inline ostream& operator << (ostream& o, const Params& p)
{
	o << "tv_n=" << p.tv_n;
	GLOBAL_PARAMS
	for(int i=0; i < p.tv_n; i++) o << ", trk=" << i << p.tv[i];
	return o;
}
#undef PARAM

//------------------------------------------------------------------------------------------
class ParamsVec
// FIFO of previous GVals & TVals, return interpolated values parameter values
{
public:
	struct Node : public Params
	{
		long pos_abs;			// absolute sample position for these params
		long samps_per_tick;	// number of samples to the next tick
		float ticks_per_samp;	// 1/samps_per_tick
	};

	vector<Node> v;				// vector of parameters
	Node *in;					// input position
	Node *curr;					// current output params
	Node *next;					// next set of output params
	Params filled_curr, filled_prev; // all gvals & tvals filled

	float tick_frac;			// current tick fraction between curr & next

public:
	ParamsVec();

	void reset(void);

	// return filled params
	const Params& getFilledCurr() { return filled_curr; }
	const Params& getFilledPrev() { return filled_prev; }

	// next is not necessarily filled
	const Params& getCurr() { return *curr; }
	const Params& getNext() { return *next; }

	// check whether the next params have been entered
	bool existsNext() const
		{ return curr != in; }

	// find the absolute tick position
	long absTickPosNext() const
		{ return next->pos_abs; }

	// go to the next param
	void incOutPos();

	int nTrkVals() const
		{ return curr->tv_n; }

	// return samples per tick of current output param set
	long sampsPerTick() const
		{ return curr->samps_per_tick; }

	// return true if the current output is invalid (initial param set)
	bool initialParam() const
		{ return in->samps_per_tick == -1; }

	// get current input node
	Node& getIn()
		{ return *in; }

	// increment the input
	void incIn()
		{ if(++in == (&v[0]+v.size())) in = &v[0]; }

	// notify in param updated
	void inUpdate()
	{
		if(in == curr) {
			// nothing in FIFO
			filled_curr.update(*curr);
			filled_prev.copy(filled_curr);
		}
	}

	// return true if the FIFO is full
	bool full() const
		{ return in+1==(&v[0]+v.size())? curr==&v[0] : in+1==curr; }

	// set fraction for interpolation of output params
	void setFrac(long pos_abs)
	{
		tick_frac = limit_range(
			(pos_abs-curr->pos_abs)*curr->ticks_per_samp, 0.0f, 1.0f
		);
	}
};

//------------------------------------------------------------------------------------------
template <class P> P interp(ParamsVec& pv, P prev_param, P curr_param, P next_param)
//
// template function to interpolate a param value from a ParamsVec,
// use the PV_INTERP macro for a simpler interface
//
{
	float t;
	switch(pv.filled_curr.interp_mode) {
	case 1: // no interpolation
		t = curr_param.data;
		break;
	case 2: // continue interp using previous interp
		t = lin_interp(pv.tick_frac+1.0f, prev_param.data, curr_param.data);
		break;
	default:// normal interp
		if(next_param.isNoVal()) t = curr_param.data;
		else t = lin_interp(pv.tick_frac, curr_param.data, next_param.data);
	}

	P r;
	r.set(t);
	return r;
}

// macro to make above "interp" more convenient
#define PV_INTERP(/*ParamsVec&*/PV, /*ParamsVec::<member variable name>*/NAME) \
	interp(PV, PV.getFilledPrev().NAME, PV.getFilledCurr().NAME, PV.getNext().NAME)


#endif
