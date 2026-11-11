// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
// DT block fx
//
// See DT_block_fx.html for more information
//
// History
// Date       Version    Programmer         Comments
// 16/2/03    1.0        Darrell Tam		Created
//

#include "stdafx.h"

//#include "FxSetDialog.h"

#include "BlkFxProcess.h"
#include "params.h"
#include "wrap_process.h"
#include "FreqToNote.h"
#include "get_info.h"

#include "BlkFxDialog.h"
#include "MIBlkFx.h"

using namespace std;

 

#ifdef _DEBUG
ofstream debug("c:\\t.1");
#endif


//------------------------------------------------------------------------------------------
// plans for performing FFT/IFFT
rfftw_plan plan_fft[N_FFT_BLK_SZ], plan_ifft[N_FFT_BLK_SZ];
static void buildFFTPlans()
{
	for(int i = 0; i < N_FFT_BLK_SZ; i++) {
		plan_fft[i] = rfftw_create_plan(GET_BLK_SZ(i), FFTW_REAL_TO_COMPLEX, FFTW_ESTIMATE);
		plan_ifft[i] = rfftw_create_plan(GET_BLK_SZ(i), FFTW_COMPLEX_TO_REAL, FFTW_ESTIMATE);
	}
}
static void destroyFFTPlans()
{
	for(int i = 0; i < N_FFT_BLK_SZ; i++) {
		rfftw_destroy_plan(plan_fft[i]);
		rfftw_destroy_plan(plan_ifft[i]);
	}
}
static CallCreateDestroy _fft_plans(buildFFTPlans, destroyFFTPlans);

//------------------------------------------------------------------------------------------
MIBlkFx::MIBlkFx()
{	
	common->mi = this;

	// stuff for buzz
	#ifndef ALL_PARAMS_GLOBAL
		TrackVals = params.tv;
		params.tv_n = 0;
	#else
		TrackVals = NULL;
		params.tv_n = N_FX_PARAM_SETS;
	#endif

	GlobalVals = &params;
	AttrVals = NULL;

	for(int i = 0; i < N_FX_PARAM_SETS; i++) 
		{ blk_fx_process[i] = BlkFxProcess::create(EffectTrkParam::DEF_VAL, this); blk_fx_process[i]->set_index=i; }


	reset();
}
//------------------------------------------------------------------------------------------
MIBlkFx::~MIBlkFx() {}

//------------------------------------------------------------------------------------------
long MIBlkFx::getBlkLenDisp(int r)
// get the currently displayed block length
{
	unsigned long n = params_disp.tick_delay.getSamples(getSamplesPerTick());	
	while(GET_BLK_SZ(r-1) > n) r--;
	return r;
}

//------------------------------------------------------------------------------------------
float MIBlkFx::roundFreqDisp(float freq)
// round frequency according to block length
{
	float sps = getSamplesPerSec();
	long blk_len = GET_BLK_SZ(getBlkLenDisp(params_disp.blk_len));
	float s = (float)blk_len/sps;
	long bin = limit_range(freq*s+0.5f, 0, blk_len/2);
	return (float)bin/blk_len*sps;
}

//------------------------------------------------------------------------------------------
void MIBlkFx::reset(void)
{
	params_vec.reset();

	x0i_abs = 0;
	x0_i = 0;
	x0_n = 0;
	x0_lastreal_abs = 0;

	x3o_abs = 0;
	x3_o = 0;
	x3_lastreal_abs = 0;

	prev_blkend_abs = 0;
	out_delay_n = 0;

	buffering_block = false;

	curr_blk_abs = 0;
	blk_len = 0;
	blk_len_fft = 0;

	prev_work_mode = 0;
}


//------------------------------------------------------------------------------------------
void MIBlkFx::Init(CMachineDataInput * const pi)
{
	this_machine = 0;
    x0.resize(max_blk_sz+2048); // allow extra for input block size
    x1.resize(max_blk_sz+1); // allow 1 extra for imaginary part of DC (which is always 0)
    x2.resize(max_blk_sz);
	x3.resize(x3_len);
}

//------------------------------------------------------------------------------------------
void MIBlkFx::SetNumTracks(int const n)
{
	#ifndef ALL_PARAMS_GLOBAL
	params.tv_n = n;
	#endif
}

//------------------------------------------------------------------------------------------
void MIBlkFx::Tick(void)
{
	params_disp.update(params);

	ParamsVec::Node* n;

	if(params_vec.initialParam() || // first param
		params_vec.getIn().pos_abs == x3o_abs || // no new audio
		params_vec.full() // can't fit another param update
	) {
		// stay on the same param
		n = &params_vec.getIn();
		n->update(params);
	}
	else {
		// go to the next param
		params_vec.incIn();
		n = &params_vec.getIn();
		n->copy(params);
		n->pos_abs = x3o_abs;
	}

	n->samps_per_tick = getSamplesPerTick();
	n->ticks_per_samp = 1.0f/n->samps_per_tick;

	params_vec.inUpdate();

	common->updateControls();
}

//------------------------------------------------------------------------------------------

//------------------------------------------------------------------------------------------
void MIBlkFx::doFFT(void)
// perform FFT, find accumulative power of spectrum & scale to prevent overflow when
// doing effects
{
	// do the FFT
	rfftw_one(plan_fft[plan_n], &x0[x0_i], &x1[0]);

	// scale spectrum
	float scale = 1e-3f/blk_len_fft;

	// set imaginary part of dc to be 0 (so other classes can treat 0hz as
	// a complex number if they wish)
	x1[blk_len_fft] = 0; 

	pcplxf in(&x1[0], blk_len_fft);
	float* end = &x1[blk_len_fft/2];

	// 0hz is real only
	(*in).real() *= scale;
	float acc = (*in).real() * (*in).real(); 
	in++;

	for(; in.r < end; in++) {
		*in = *in * scale;
		acc += norm(*in);
	}

	// nyquist freq is real only
	(*in).real() *= scale;
	acc += (*in).real() * (*in).real(); 

	total_out_pwr = acc;
	total_in_pwr = acc;
}


//------------------------------------------------------------------------------------------
void MIBlkFx::doIFFT(void)
{
	// perform inverse FFT
	rfftw_one(plan_ifft[plan_n], &x1[0], &x2[0]);

	// set to undo scaling & match input power
	double s = PV_INTERP(params_vec, out_amp).getMult()*1e3;
	if(total_out_pwr > 0) s *= sqrt(total_in_pwr/total_out_pwr);
	if(s > 1e30) x2_scale = 1.0f;
	else if(s < 1e-30) x2_scale = 0.0f;
	else x2_scale = (float)s;

	//debug << scientific << "x2_scale=" << x2_scale << ", in_pwr=" << in_pwr << ", total_out_pwr="<<total_out_pwr<<endl;
}

//------------------------------------------------------------------------------------------
void MIBlkFx::processFFT(void)
//
// process the FFT'd data (x1) using the fx_params
//
{
	long i;	
	for(i = 0; i < params_vec.nTrkVals(); i++) {
		// get the parameters for this processing

		_FreqParam freq_a = PV_INTERP(params_vec, tv[i].freq_a);
		_FreqParam freq_b = PV_INTERP(params_vec, tv[i].freq_b);

		// do nothing if the effect is off
		if(freq_a.get() == 0.0f && freq_b.get() == 0.0f) continue;

		// get current params
		float s = (float)blk_len_fft/getSamplesPerSec();
		fcurr_start_bin = limit_range(freq_a.getHz()*s, 1, blk_len_fft/2);
		fcurr_stop_bin = limit_range(freq_b.getHz()*s, 1, blk_len_fft/2);
		curr_start_bin = fcurr_start_bin+0.5f;
		curr_stop_bin = fcurr_stop_bin+0.5f;
		curr_amp = PV_INTERP(params_vec, tv[i].amp).getMult();
		curr_fxval = PV_INTERP(params_vec, tv[i].fxval).getFrac();

		// run the effect
		int effect_n = params_vec.getFilledCurr().tv[i].effect.limitGet();

		// change the effect?
		if(blk_fx_process[i]->getEffectNum() != effect_n)
			{ blk_fx_process[i] = BlkFxProcess::create(effect_n, this); blk_fx_process[i]->set_index=i; }

		blk_fx_process[i]->process();
	}
	x1[0] = 0; // too hard to deal with DC
}


//------------------------------------------------------------------------------------------
void MIBlkFx::mixToOutputBuffer(void)
//
// mix from x2 (post-IFFT) & x0 (pre-FFT) into x3
//
{
	// number of samples over which current blk fades in over
	long fadein_n = prev_blkend_abs - curr_blk_abs;

	// check whether there's an overlap
	if(fadein_n < 0) {
		// arbitary fade out of previous data

		// offsets relative to x3_o
		long prev_blkend_rel = prev_blkend_abs - x3o_abs;
		long fade_start_rel = prev_blkend_rel - BLK_SZ_0;

		// don't fade data that has already been output
		if(fade_start_rel < 0) fade_start_rel = 0;

		long n = prev_blkend_rel-fade_start_rel;
		if(n > 0) wrapProcess(PFadeOut(n),x3, wrap(x3_o+fade_start_rel, x3), n);

		// zero fill if there's a gap between end of previous block and
		// fade-in of this block
		n = -fadein_n;
		fadein_n = BLK_SZ_0; // arbitary fade in of new block
		wrapProcess(PZero(), x3, wrap(x3_o+prev_blkend_rel, x3), n+fadein_n);
	}

	fadein_n = limit_range(fadein_n, 0, fade_samps);

	// absolute start position of current blk into the output buffer
	long curr_blk_i = wrap(x3_o+curr_blk_abs-x3o_abs, x3);

	if(mix_back <= 0.0f) {
		// output data is completely processed
		if(fadein_n > 0)
			wrapProcess(PXFade(&x2[0], x2_scale, fadein_n), x3, curr_blk_i, fadein_n);

		wrapProcess(PScaleCopy(&x2[fadein_n], x2_scale),
			x3, wrap(curr_blk_i+fadein_n, x3), blk_len-fadein_n);
	}
	else if(mix_back >= 1.0f) {
		// output data is completely original
		if(fadein_n > 0)
			wrapProcess(PXFade(&x0[x0_i], 1.0f, fadein_n), x3, curr_blk_i, fadein_n);

		wrapProcess(PScaleCopy(&x0[x0_i+fadein_n], 1.0f),
			x3, wrap(curr_blk_i+fadein_n, x3), blk_len-fadein_n);
		//wrapProcess(PScaleCopy(&x0[x0_i], 1.0f),
		//	x3, wrap(curr_blk_i, x3), blk_len);
	}
	else {
		// output data is a mix of original and processed
		x2_scale *= 1.0f-mix_back; // processed data
		float x0_scale = mix_back; // original data

		if(fadein_n > 0)
			wrapProcess(PXFade2(&x2[0], x2_scale, &x0[x0_i], x0_scale, fadein_n),
				x3, curr_blk_i, fadein_n);

		wrapProcess(PScaleCopy2(&x2[fadein_n], x2_scale, &x0[x0_i+fadein_n], x0_scale),
			x3, wrap(curr_blk_i+fadein_n, x3), blk_len-fadein_n);
	}
}

//------------------------------------------------------------------------------------------
bool MIBlkFx::paramsRdy(void)
//
// return true if we can interpolate the current block of data or the output delay
// is too short
//
{
	if(params_vec.existsNext()) {
		// parameters can be interpolated as there is a next value
		params_vec.setFrac(x0i_abs);

		// get interpolated params for this tick

		// update the output delay only if the tick delay was explicitly set
		if(!params_vec.getCurr().tick_delay.isNoVal())
			out_delay_n = PV_INTERP(params_vec, tick_delay).getSamples(params_vec.sampsPerTick());
		curr_blk_abs = out_delay_n+x0i_abs;

		plan_n = PV_INTERP(params_vec, blk_len);
		blk_len_fft = GET_BLK_SZ(plan_n);

		buffering_block = true;
		return true;
	}

	// do nothing if this is the initial (default) param
	if(params_vec.initialParam()) return false;

	// we can't interpolate the tick delay params yet but check whether
	// the tick delay on the previously encountered param forces a block
	// to be output
	TickDelayParam tick_delay = params_vec.getCurr().tick_delay;
	if(!tick_delay.isNoVal())
		out_delay_n = tick_delay.getSamples(getSamplesPerTick());
	curr_blk_abs = out_delay_n+x0i_abs;

	// if the next block doesn't need to be output, do nothing
	if(curr_blk_abs - x3o_abs >= buf_n) return false;

	// get current params (non-interpolated) params
	plan_n = params_vec.getFilledCurr().blk_len.limitGet();
	blk_len_fft = GET_BLK_SZ(plan_n);

	buffering_block = true;
	return true;
}


//------------------------------------------------------------------------------------------
bool MIBlkFx::blkRdy(void)
//
// return true if there's enough data to process the current block or we are forced
// to output
//
{
	// are we forced to output a block?
	if(curr_blk_abs - x3o_abs < buf_n) {

		// check how much data is in the input buffer and reduce block size if not enough
		// so as to reduce processing load 
		while(GET_BLK_SZ(plan_n-1) > x0_n) plan_n--;
		if(plan_n < 0) plan_n = 0;

		blk_len_fft = GET_BLK_SZ(plan_n);
		
		// check how much data is actually available
		long n = blk_len_fft-x0_n;
		if(n <= 0) blk_len = blk_len_fft;
		else {
			// need to zero-pad the pre-fft buffer as there isn't enough data
			blk_len = x0_n;

			// delete old data if there's not enough room
			if(x0_i+blk_len_fft > x0.size()) {
				if(x0_n > 0) memcpy(&x0[0], &x0[x0_i], x0_n*sizeof(float));
				x0_i = 0;
			}

			// zero unfilled data
			memset(&x0[x0_i+blk_len], 0, n*sizeof(float));
		}
		return true;

	}

	// is there enough data to process the block?
	if(x0_n >= blk_len_fft) {
		blk_len = blk_len_fft;
		return true;
	}

	// not enough data to process block
	return false;
}

//------------------------------------------------------------------------------------------
void MIBlkFx::nextBlk(void)
//
// update everything ready for the next block
//
{
	// determine overlap with next block
	long nonoverlap_n = limit_range(
		blk_len-fade_samps,
		8, blk_len
	);

	// check for block sync & increment params_vec
	while(params_vec.existsNext()) {
		long tick_rel = params_vec.absTickPosNext() - x0i_abs;
		if(tick_rel > nonoverlap_n) break;
		params_vec.incOutPos();
		BlkSyncParam blk_sync = params_vec.getCurr().blk_sync;
		if(!blk_sync.isNoVal() && blk_sync.limitGet()) {
			// force next block to start on this tick
			/*debug << "forcing tick sync"
				<< ", nonoverlap_n=" << nonoverlap_n
				<< ", tick_rel=" << tick_rel
				<< endl;*/
			nonoverlap_n = tick_rel;
			break;
		}
	}

	// update positions	
	x0_i += nonoverlap_n;
	x0_n -= nonoverlap_n;
	x0i_abs += nonoverlap_n;

	long blkend_abs = curr_blk_abs + blk_len;

	if(blkend_abs-prev_blkend_abs > 0) prev_blkend_abs = blkend_abs;
	buffering_block = false;
}

//------------------------------------------------------------------------------------------
bool MIBlkFx::Work(float *buf, int _buf_n, int const work_mode)
{
	buf_n = _buf_n;

	if (work_mode == WM_NOIO) return false;
	if (work_mode == WM_READ) return true;

	if(work_mode == WM_WRITE) {
		// writing has finished, determine whether we need to keep outputting

		if(prev_work_mode != WM_WRITE) {
			// first block after real data
			x0_lastreal_abs = x3o_abs+buf_n; // amount of data remaining to be processed
			x3_lastreal_abs = curr_blk_abs+blk_len; // end of previous block
		}

		if(x0i_abs-x0_lastreal_abs <= 0) {
			// haven't finished with the real input data, keep track of where
			// the block that it belongs to will end
			long t = curr_blk_abs+blk_len;
			if(t-x3_lastreal_abs > 0) x3_lastreal_abs = t;
		}
		else if(x3o_abs-x3_lastreal_abs > 0) {
			// all done when we pass the end of the last block to contain real data
			return false;
		}

		// input has stopped, still writing
		memset(buf, 0, buf_n*sizeof(float));
	}

	// shift out used data if not enough room for the new buffer
	if(x0_i+x0_n+buf_n > x0.size()) {
		// hopefully this won't happen - increase buffer length if too full
		if(x0_n+buf_n > x0.size()) x0.resize(x0_n+buf_n);

		// shift out old data
		if(x0_n > 0) memcpy(&x0[0], &x0[x0_i], x0_n*sizeof(float));
		x0_i = 0;
	}

	// copy input data into the pre-fft buffer
	memcpy(&x0[x0_i+x0_n], buf, buf_n*sizeof(float));
	x0_n += buf_n;

	while(1) {
		if(!buffering_block) {
			if(!paramsRdy()) break;
		}

		if(buffering_block) {
			if(!blkRdy()) break;

			// 
			fade_samps = PV_INTERP(params_vec, overlap).getOverlapFrac()*blk_len;

			// fraction of original data
			mix_back = PV_INTERP(params_vec, mix_back).getFrac();
			if(mix_back < 1.0f) {
				doFFT();
				common->sgramData(0, x0i_abs, plan_n, pcplxf(&x1[0], blk_len_fft));
				processFFT();
				common->sgramData(1, x0i_abs, plan_n, pcplxf(&x1[0], blk_len_fft));
				doIFFT();
			}
			mixToOutputBuffer();
			nextBlk();
		}
	}

	// copy from x3 (output FIFO) to the output buffer
	wrapProcess(PCopyOut(buf), x3, x3_o, buf_n);

	// adjust output pointers
	x3_o = wrap(buf_n+x3_o, x3);
	x3o_abs += buf_n;

	prev_work_mode = work_mode;

	return true;
}

//------------------------------------------------------------------------------------------

//------------------------------------------------------------------------------------------

//------------------------------------------------------------------------------------------
