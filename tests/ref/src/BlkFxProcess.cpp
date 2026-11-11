// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************
DT Block FX - classes to perform actual effects on the FFT'd data

BlkFxProcess
	Base class for effects

BlkFxProcess*
	Individual classes to perform a particular effect on the FFT spectrum

History
	Date       Version    Programmer         Comments
	16/2/03    1.0        Darrell Tam		Created
******************************************************************************/

#include "stdafx.h"

#include <SinCosTable.h>

#include "ParamDesc.h"
#include "MIBlkFx.h"
#include "process_loops.h"
#include "BlkFxProcess.h"

static SinCosTable<cplxf, /*bits*/12> sincos_table;


//******************************************************************************************
class CreateBlkFx
{
public:
	virtual BlkFxProcess::AP create() const = 0;

	int desc_table_sz;
	ParamDesc** desc_table;
	const char* name;
};
template <class T> class CreateBlkFxT : public CreateBlkFx
{

public:
	BlkFxProcess::AP create() const { return BlkFxProcess::AP(new T); }

	CreateBlkFxT(const char* name_, ParamDesc** desc_table_, int desc_table_sz_)
	{
		name = name_;
		desc_table = desc_table_;
		desc_table_sz = desc_table_sz_;
	}
};


//******************************************************************************************
// BlkFx methods
BlkFxProcess::BlkFxProcess() { }
BlkFxProcess::~BlkFxProcess() { }

//------------------------------------------------------------------------------------------
void BlkFxProcess::matchPwr(pcplxf_range x, float in_pwr, float out_pwr)
// match output power to input power multiplied by curr_amp^2
{
	float curr_amp = mi->curr_amp;
	double s = curr_amp;
	if(out_pwr > 0) s *= sqrt(in_pwr/out_pwr);
	float scale;

	// limit the scaling
	if(s > 1e30) scale = 1.0f;
	else if(s < 1e-30) scale = 0.0f;
	else scale = (float)s;

	for(; x.r != x.e; x++) *x = (*x) * scale;

	// adjust total output power according to amp
	mi->total_out_pwr += (curr_amp*curr_amp-1)*in_pwr;
}

//------------------------------------------------------------------------------------------
void BlkFxProcess::process(void)
// default BlkFx process calls 
{
	float *x1 = &(mi->x1[0]);
	long blk_len_fft = mi->blk_len_fft;
	long curr_start_bin = mi->curr_start_bin;
	long curr_stop_bin = mi->curr_stop_bin;
	if(curr_start_bin <= curr_stop_bin) {
		// freqA < freqB : normal process - do the region specified
		process(pcplxf_range(x1, blk_len_fft, curr_start_bin, curr_stop_bin));
	}
	else {
		// freqA > freqB : process in 2 parts - everything except for the specified region
		process(pcplxf_range(x1, blk_len_fft, 1, curr_stop_bin+1));
		process(pcplxf_range(x1, blk_len_fft, curr_start_bin, blk_len_fft/2));
	}
}

//------------------------------------------------------------------------------------------
void BlkFxProcess::process(pcplxf_range x)
{
	float curr_amp = mi->curr_amp;
	if(curr_amp == 1.0f) return;
	float in_pwr = 0;

	for(; x.r != x.e; x++) {
		in_pwr += norm(*x);
		*x = (*x) * curr_amp;
	}
	
	// adjust output power according to amp
	mi->total_out_pwr += (curr_amp*curr_amp-1)*in_pwr;

}

//******************************************************************************************
class BlkFxContrast : public BlkFxProcess
{
public:

	//--------------------------------------------------------------------------------------
	void process(pcplxf_range x, float raise, bool raise_neg)
	// Change the contrast of the spectrum
	//
	// perform: abs(X)^(2.raise+1) on part of the spectrum
	//
	// raise_neg should be passed as a constant to help optimizer
	{
		float in_pwr = 0; // input power (pre-effect)
		float out_pwr = 0; // output power (post-effect)
		pcplxf_range t = x;

		// set limits to prevent overflow
		float limit;
		float r = raise*2;
		if(r < 0) {
			if(r >= -1) limit = 1e-28f;
			else limit = powf(1e28f, 1.0f/r);
		}
		else {
			if(r < 1) limit = 1e28f;
			else limit = powf(1e28f, 1.0f/r);
		}

		for(; x.r != x.e; x++) {
			float t0 = norm(*x);
			in_pwr += t0;
			float t1;
			if(raise_neg) {
				if(t0 < limit) t1 = 1.0f;
				else t1 = powf(t0, raise);
			}
			else {
				if(t0 > limit) t0 = limit;
				t1 = powf(t0, raise);
			}
			out_pwr += t0*t1*t1;
			*x = (*x) * t1;
		}
		matchPwr(t, in_pwr, out_pwr);
	}

	//--------------------------------------------------------------------------------------
	virtual void process(pcplxf_range x)
	{
		float t = mi->curr_fxval;
		if(t == 0.0f) {
			BlkFxProcess::process(x); // effect off
			return;
		}
		t -= 0.5f;
		if(t >= 0.0f) process(x, lin_interp(t*t*4.0f, 0.0f, 4.0f), false);
		else process(x, lin_interp(t*t*4.0f, 0.0f, -0.5f), true);
	}

};
static ParamDesc* contrast_params[] = { param_desc_percent2, param_desc_hex };
static CreateBlkFxT<BlkFxContrast>
	create_blkfx_contrast("Contrast", contrast_params, NUM_ELEMENTS(contrast_params));

//******************************************************************************************
class BlkFxSmear : public BlkFxProcess
{
public:
	//--------------------------------------------------------------------------------------
	void process(pcplxf_range x)
	//
	// randomize the phase
	//
	{
		float smear = mi->curr_fxval;
		float in_pwr = 0;
		if(smear == 0.0f) {
			BlkFxProcess::process(x);
			return;
		}

		float curr_amp = mi->curr_amp;
		if(!_inited){ int sd=(0x1234567 + set_index*0x9E3779) & 0x1FFFFFFF; _rand_i = sd?sd:1; _inited=true; }
		long rand_i = _rand_i;

		for(; x.r != x.e; x++) {
			int angle = rand_i & sincos_table.IMASK;
			in_pwr += norm(*x);
			*x = (*x) * (sincos_table[angle]*smear+1.0f-smear) * curr_amp;
			rand_i = prbs29(rand_i);
		}

		// adjust output power according to amp
		mi->total_out_pwr += (curr_amp*curr_amp-1)*in_pwr;
		_rand_i = rand_i;
	}

	long _rand_i; bool _inited;
	BlkFxSmear():_inited(false){}
};
static ParamDesc* smear_params[] = { param_desc_percent, param_desc_hex };
static CreateBlkFxT<BlkFxSmear> create_blkfx_smear("Smear", smear_params, NUM_ELEMENTS(smear_params));

//******************************************************************************************
class BlkFxWeed : public BlkFxProcess
{
public:

	//--------------------------------------------------------------------------------------
	void process(pcplxf_range x)
	// weed out bins with magnitude lower than the weed threshold
	{
		pcplxf_range t = x;
		float in_pwr = 0;
		float out_pwr = 0;

		// scale threshold: -1 .. 1
		float thresh = mi->curr_fxval*2.0f-1.0f; 

		if(thresh >= 0) {
			// weed (set to 0) any bins lower than the threshold (arbitary scaling)
			thresh *= thresh*24.0f*mi->total_out_pwr/mi->blk_len_fft;

			for(; x.r != x.e; x++) {
				float t0 = norm(*x);
				in_pwr += t0;
				if(t0 < thresh) *x = cplxf(0,0);
				else out_pwr += t0;
			}
		}
		else {
			// weed (set to 0) any bins higher than the threshold (arbitary scaling)
			thresh = 1+thresh;
			thresh *= thresh*24.0f*mi->total_out_pwr/mi->blk_len_fft;

			float rt = sqrtf(thresh);

			for(; x.r != x.e; x++) {
				float t0 = norm(*x);
				in_pwr += t0;
				if(t0 >= thresh) *x = cplxf(0,0);
				else out_pwr += t0;
			}
		}

		matchPwr(t, in_pwr, out_pwr);
	}
};
static ParamDesc* weed_params[] = { param_desc_percent2, param_desc_hex };
static CreateBlkFxT<BlkFxWeed> create_blkfx_weed("Weed", weed_params, NUM_ELEMENTS(weed_params));

//******************************************************************************************
class BlkFxClip : public BlkFxProcess
{
public:
	//--------------------------------------------------------------------------------------
	void process(pcplxf_range x)
	// clip bins with magnitude higher than the threshold to the threshold
	{
		pcplxf_range t = x;

		float in_pwr = 0;
		float out_pwr = 0;

		// arbitary threshold function
		float thresh = 1-mi->curr_fxval;
		thresh *= thresh*8.0f*mi->total_out_pwr/mi->blk_len_fft;

		float rt = sqrtf(thresh);

		for(; x.r != x.e; x++) {
			float t0 = norm(*x);
			in_pwr += t0;
			if(t0 >= thresh && t0 > 1e-37f) {
				float t1 = rt / sqrtf(t0);
				*x = (*x) * t1;
				out_pwr += thresh;
			}
			else out_pwr += t0;
		}
		
		matchPwr(t, in_pwr, out_pwr);
	}
};
static ParamDesc* clip_params[] = { param_desc_percent, param_desc_hex };
static CreateBlkFxT<BlkFxClip> create_blkfx_clip("Clip", clip_params, NUM_ELEMENTS(clip_params));

//******************************************************************************************
//------------------------------------------------------------------------------------------
template <class A> inline float shiftApply(
	A& a,
	vector<float>& x,	// FFT spectrum as prduced by rfftw
	long fft_len,
	long start_bin,		// must be in range of fft size
	long dest_bin,		// can be out of range
	long n_bins,		// number of FFT bins
	float curr_amp		// amplitude scale
)
// instead of using virtual functions, this is implemented as a template so as to help optimizer
//
// return the change in power after applying effect
{
	// adjust ranges if destination goes outside of fft data
	if(dest_bin < 1)  {
		start_bin += (1-dest_bin);
		n_bins -= (1-dest_bin);
		dest_bin = 1;
	}
	if(dest_bin+n_bins >= fft_len/2-1) n_bins = fft_len/2-1 - dest_bin;

	if(n_bins < 1) return 0.0f;

	float pwr = 0;
	if(start_bin > dest_bin) {
		// process spectrum forwards
		pcplxf xs(&x[0], fft_len, start_bin);
		pcplxf xd(&x[0], fft_len, dest_bin);
		float *xe = xs.r+n_bins;
		for(; xs.r < xe; xs++, xd++) {
			pwr -= norm(*xd);
			a.apply(xs, xd, curr_amp);
			pwr += norm(*xd);
		}
	}
	else {
		// process spectrum in reverse
		pcplxf xs(&x[0], fft_len, start_bin+n_bins-1);
		pcplxf xd(&x[0], fft_len, dest_bin+n_bins-1);
		float *xe = xs.r-n_bins;
		
		for(; xs.r > xe; xs--, xd--) {
			pwr -= norm(*xd);
			a.apply(xs, xd, curr_amp);
			pwr += norm(*xd);
		}
	}
	return pwr;
}

//------------------------------------------------------------------------------------------
template <class A> inline float shiftApply(MIBlkFx* mi, A a)
//
// determine whether to process inside or outside of frequency range
// template to help optimizer
//
// return the change in power
{
	float pwr;

	long sps = mi->getSamplesPerSec();
	long shift = freqShift(mi->curr_fxval)/sps*mi->blk_len_fft+0.5f;
	if(mi->curr_start_bin <= mi->curr_stop_bin) {
		// inside frequency range
		pwr = shiftApply(a, mi->x1, mi->blk_len_fft,
			mi->curr_start_bin, mi->curr_start_bin+shift, mi->curr_stop_bin-mi->curr_start_bin+1,
			mi->curr_amp);

	}
	else {
		// exclude frequency range
		pwr = shiftApply(a, mi->x1, mi->blk_len_fft, 1, 1+shift, mi->curr_stop_bin,
			mi->curr_amp);

		pwr += shiftApply(a, mi->x1, mi->blk_len_fft,
			mi->curr_start_bin, mi->curr_start_bin+shift, mi->blk_len_fft/2-mi->curr_start_bin,
			mi->curr_amp);
	}	
	return pwr;
}

//******************************************************************************************
struct BlkFxShiftAdd : public BlkFxProcess
{
public:
	class Apply {
	public: inline void apply(pcplxf src, pcplxf dst, float curr_amp) { *dst = *src * curr_amp + *dst; }
	};

	virtual void process(void)
	{
		mi->total_out_pwr += shiftApply(mi, Apply());
	}

};
static ParamDesc* shift_add_params[] = { param_desc_freq_shift, param_desc_hex };
static CreateBlkFxT<BlkFxShiftAdd> create_blkfx_shift_add("ShiftAdd", shift_add_params, NUM_ELEMENTS(shift_add_params));

//******************************************************************************************
struct BlkFxShiftReplace : public BlkFxShiftAdd
{
public:
	class Apply {
	public: inline void apply(pcplxf src, pcplxf dst, float curr_amp) { *dst = *src * curr_amp; }
	};

	virtual void process(void) { mi->total_out_pwr += shiftApply(mi, Apply()); }
};
static CreateBlkFxT<BlkFxShiftReplace>
	create_blkfx_shift_replace("ShiftReplace", shift_add_params, NUM_ELEMENTS(shift_add_params));

//******************************************************************************************
struct BlkFxHarmonic : public BlkFxProcess
{
public:
	//--------------------------------------------------------------------------------------
	void process(float centre, float spacing, float frac, long stop_bin)
	// Change the amplitude of harmonics centred at <start_bin>, <spacing> bins apart
	{
		// number of bins to do each side of the harmonic
		float n = limit_range(spacing*frac/2, 0, spacing/2);

		// nothing to do
		if(n < 1) return;

		float amp = mi->curr_amp;
		long blk_len_fft = mi->blk_len_fft;
		float* x1 = &(mi->x1[0]);

		float pwr = 0;
		float bin = centre-n;
		float* stop = &(x1[stop_bin]);
		n*=2;
		bool looping = true;
		while(looping) {
			long b = limit_range((long)(bin+0.5f), 0, blk_len_fft/2);
			pcplxf x(&(x1[0]), blk_len_fft, b);
			float* xe = &(x1[(long)(bin+0.5f+n)]);
			if(xe >= stop) {
				xe = stop-1;
				looping = false;
			}
			for(; x.r <= xe; x++) {
				pwr += norm(*x);
				*x = (*x)*amp;
			}
			bin += spacing;
		}
		mi->total_out_pwr += (amp*amp-1)*pwr;
	}
	//--------------------------------------------------------------------------------------
	void process(void)
	{
		SplitHarmonicParam p(mi->curr_fxval);
		switch(p.harm_type)
		{
		case 0://both
			process(mi->fcurr_start_bin, mi->fcurr_start_bin, p.width_frac, mi->curr_stop_bin);
			break;
		case 1://odd
			process(mi->fcurr_start_bin, mi->fcurr_start_bin*2,	p.width_frac, mi->curr_stop_bin);
			break;
		case 2://even
			process(mi->fcurr_start_bin*2, mi->fcurr_start_bin*2, p.width_frac, mi->curr_stop_bin);
			break;
		}	
	}
};
static ParamDesc* harmonic_params[] = { param_desc_harmonic_type, param_desc_harmonic_width, param_desc_hex };
static CreateBlkFxT<BlkFxHarmonic>
	create_blkfx_harmonic("Harmonic", harmonic_params, NUM_ELEMENTS(harmonic_params));

//******************************************************************************************
struct BlkFxAutoHarmonic : public BlkFxHarmonic
{
public:
	void process(void)
	// auto amplify loudest harmonic
	{
		long start_bin = mi->curr_start_bin;
		long stop_bin = mi->curr_stop_bin;
		long process_stop_bin = stop_bin;

		if(start_bin > stop_bin) {
			// if freqA < freqB, processing occurs over the full spectrum
			swap(start_bin, stop_bin);
			process_stop_bin = mi->blk_len_fft/2;
		}

		// find the bin having the highest power in the freq range specified
		pcplxf x(&(mi->x1[0]), mi->blk_len_fft, start_bin);
		float* xe = &(mi->x1[stop_bin]);

		float* x_pos = 0;
		float max_pwr = 0.0f;
		for(; x.r < xe; x++) {
			float t = norm(*x);
			if(t > max_pwr) {
				max_pwr = t;
				x_pos = x.r;
			}
		}
		
		if(max_pwr == 0.0f) return; // nothing found...

		// find position of harmonic
		long harmonic = x_pos-&(mi->x1[0]);

		SplitHarmonicParam p(mi->curr_fxval);
		switch(p.harm_type)
		{
		case 0://both
			BlkFxHarmonic::process(harmonic, harmonic, p.width_frac, process_stop_bin);
			break;
		case 1://odd
			BlkFxHarmonic::process(harmonic, harmonic*2, p.width_frac, process_stop_bin);
			break;
		case 2://even
			BlkFxHarmonic::process(harmonic*2, harmonic*2, p.width_frac, process_stop_bin);
			break;
		}
	}
};
static CreateBlkFxT<BlkFxAutoHarmonic>
	create_blkfx_auto_harmonic("AutoHarm", harmonic_params, NUM_ELEMENTS(harmonic_params));

//******************************************************************************************
struct BlkFxCopyHarmonic : public BlkFxHarmonic
{
public:
	//--------------------------------------------------------------------------------------
	void process(float centre, float spacing, float frac, long stop_bin)
	// harmonic copy
	{
		float t = spacing*frac/2;
		float fbeg = limit_range(centre-t, 1.0f, mi->blk_len_fft/2);
		long beg = fbeg+0.5f;
		long end = limit_range(centre+t+0.5f, 1.0f, mi->blk_len_fft/2);
		
		// number of bins to copy
		long na = centre-beg+1;
		long nb = end-centre+1;
		if(na < 0.0f) na = 0.0f;
		if(nb < 0.0f) nb = 0.0f;
		long n = na+nb;

		// check whether there's anything to do
		if(n < 1 || spacing < 10) return;

		// work out scale_step
		float scale_step = 1.0f; // assume flat spectrum

		// if stop_bin is less than centre, use arbitary decay on harmonic copy
		if(stop_bin < centre && centre > 0) {
			t = lin_interp(stop_bin/centre, centre, mi->blk_len_fft/2)/spacing;
			if(t == 0.0f) return;
			scale_step = powf(0.25f, 1/t);
			stop_bin = mi->blk_len_fft/2;
		}

		// copy variables from machine interface
		float amp = mi->curr_amp;

		float* x1 = &(mi->x1[0]);
		long x1_len = mi->blk_len_fft;

		float* x2 = &(mi->x2[0]);
		long x2_len = n+n;

		// source harmonic data
		pcplxf x1a(x1, x1_len, beg);
		pcplxf x1b(x1, x1_len, beg+na);

		// use mi->x2 for temporary storage - make a copy
		// so that destination can can overlap with source
		pcplxf x2a(x2, x2_len, 0);
		pcplxf x2b(x2, x2_len, na);

		// ramp x1 & copy into x2
		float step_a, step_b;
		if(na > 0) {
			step_a = amp/na;
			rampCopy(x2a, x1a, 0, step_a, na);
		}
		if(nb > 0) {
			step_b = -amp/nb;
			rampCopy(x2b, x1b, amp, step_b, nb);
		}

		// copy harmonic in a loop
		float pwr = 0;
		float src_scale = 1.0f;
		while(1) {
			beg = fbeg+0.5;
			if(beg < 1.0f) beg = 1.0f;
			if(beg > x1_len/2) break;

			x1a(x1, x1_len, beg);
			x1b(x1, x1_len, beg+na);

			if(beg+na > stop_bin) {
				na = stop_bin - beg;
				nb = 0;
			}
			if(beg+na+nb > stop_bin) {
				nb = stop_bin - (beg+na);
			}

			if(na > 0) rampMix1(x1a, x2a, src_scale, 0, step_a, na);
			if(nb > 0) rampMix1(x1b, x2b, src_scale, amp, step_b, nb);

			fbeg += spacing;
			src_scale *= scale_step;
			if(src_scale < 1e-6) break;
		}
		mi->total_out_pwr += pwr;
	}

	//--------------------------------------------------------------------------------------
	void process(void)
	// copy data in a harmonic way
	{
		SplitHarmonicParam p(mi->curr_fxval);
		switch(p.harm_type)
		{
		case 0://both
			process(mi->curr_start_bin, mi->curr_start_bin, p.width_frac, mi->curr_stop_bin);
			break;
		case 1://odd
			process(mi->curr_start_bin, mi->curr_start_bin*2, p.width_frac, mi->curr_stop_bin);
			break;
		case 2://even
			process(mi->curr_start_bin*2, mi->curr_start_bin*2, p.width_frac, mi->curr_stop_bin);
			break;
		}
	}
};
static CreateBlkFxT<BlkFxCopyHarmonic>
	create_blkfx_copy_harmonic("CopyHarm", harmonic_params, NUM_ELEMENTS(harmonic_params));

//******************************************************************************************
//------------------------------------------------------------------------------------------
static CreateBlkFx* create_blkfx_table[] = {
	&create_blkfx_contrast,
	&create_blkfx_smear,
	&create_blkfx_clip,
	&create_blkfx_weed,
	&create_blkfx_shift_add,
	&create_blkfx_shift_replace,
	&create_blkfx_harmonic,
	&create_blkfx_auto_harmonic,
	&create_blkfx_copy_harmonic
};

//------------------------------------------------------------------------------------------
int BlkFxProcess::getTotalNumEffects()
	{ return NUM_ELEMENTS(create_blkfx_table); }

const char* BlkFxProcess::name(int effect_i)
	{ return create_blkfx_table[limitEffectNum(effect_i)]->name; }

void BlkFxProcess::getEffectDesc(int effect_i, int* desc_table_sz, ParamDesc*** desc_table)
{
	CreateBlkFx* t = create_blkfx_table[limitEffectNum(effect_i)];
	*desc_table = t->desc_table;
	*desc_table_sz = t->desc_table_sz;
}

BlkFxProcess::AP BlkFxProcess::create(int effect_i, MIBlkFx* mi)
{
	effect_i = limitEffectNum(effect_i);
	BlkFxProcess::AP t = create_blkfx_table[effect_i]->create();
	t->mi = mi;
	t->effect_num = effect_i;
	return t;
}

