// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************
DT Block FX - Buzz machine


MIBlkFx
	Buzz machine interface

	* Buffer sample data & params
	* Match params with block
	* Process overlapping blocks of data


History
	Date       Version    Programmer         Comments
	16/2/03    1.0        Darrell Tam		Created
******************************************************************************/

#ifndef _DT_MI_BLOCKFX_H_
#define _DT_MI_BLOCKFX_H_

#ifndef FFTW_ENABLE_FLOAT
#define FFTW_ENABLE_FLOAT
#endif

#include <windows_support.h>
#include <cplx.h>
#include <MachineInterface.h>
#include <rfftw.h>

#include "params.h"
#include "misc_stuff.h"
#include "MIBlkFxCommon.h"

#ifdef _DEBUG
extern ofstream debug;
#define DBG(a) a
#else
#define DBG(a) 
#endif

using namespace std;

class BlkFxProcess;
class BlkFxDialog;

//------------------------------------------------------------------------------------------
class MIBlkFx : public CMachineInterface
{
public:
	MIBlkFx();
	virtual ~MIBlkFx();

protected:
	// methods overridden from CMachineInterface
	virtual void Init(CMachineDataInput * const pi);
	virtual void Tick();
	virtual char const *DescribeValue(int const param, int const value);
	virtual bool Work(float *, int , int const );
	virtual void SetNumTracks(int const n);
	virtual void Command(int const i);

protected:	// internal methods
	void doFFT(void);
	void doIFFT(void);
	void processFFT(void);
	void mixToOutputBuffer(void);
	bool paramsRdy(void);
	bool blkRdy(void);
	void nextBlk(void);

public:
	// get estimated output position playing
	long getOutPosAbs() { return x0i_abs-out_delay_n; }

	// number of buzz tracks
	int getNumTrks() { return params.tv_n; }

	// safe control change
	void controlChange(int group/*1=gval,2=tval*/, int track, int param, int value);

	//
	int getSamplesPerSec() { return pMasterInfo->SamplesPerSec; }
	int getSamplesPerTick() { return pMasterInfo->SamplesPerTick; }
	const char* getMachineName() { return pCB->GetMachineName(this_machine); }

	// get currently displayed params
	const Params& getParamsDisp() const { return params_disp; }

	// samples = BLK_SZ_0 << getBlkLenDisp()
	long getBlkLenDisp(int v);

	// return rounded display frequency (round frequency according to 
	// FFT block length)
	float/*Hz*/roundFreqDisp(float freq/*Hz*/);

	// reset buffer pointers
	void reset(void);

public: //
	// data common to dialog and MI
	RefNew<MIBlkFxCommon> common;

	vector<float> x0;	// pre FFT buffer
	vector<float> x3;	// output FIFO

	long x0i_abs;		// absolute sample position of x0_i
	long x0_i;			// pre-fft buffer offset
	long x0_n;			// number of samples in the pre-fft buffer
	long x0_lastreal_abs;//final pre-fft position of data passed to Work()

	long x3o_abs;		// absolute sample position of x3_o/current sample
	long x3_o;			// output FIFO output index
	long x3_lastreal_abs;//final output position of data passed to Work()

	long prev_blkend_abs;// absolute end position of previous block written
	long out_delay_n;	// output delay of current block

	bool buffering_block;// false if waiting for next param

	long curr_blk_abs;	// output position for current block

	int prev_work_mode;	// mode passed to "Work()"

public: // temporary variables used during Work()
	int plan_n;			// plan number to use (corresponds to blk_len_fft)
	long blk_len_fft;	// FFT block length
	float mix_back;		// current mixback fraction
	long fade_samps;	// current number of cross-fade samples

	long blk_len;		// actual length of block to process (same as blk_len_fft unless not enough data)
	long buf_n;			// current input buffer length
	float x2_scale;		// scaling to apply to x2 during mix back
	float total_in_pwr;	// x1 input power
	float total_out_pwr;// current output power

	vector<float> x1;	// FFT'd data (frequency-domain), scaled 1e-3
	vector<float> x2;	// IFFT'd data (time-domain)

	// temporary variables for the current track during processing
	float fcurr_start_bin, fcurr_stop_bin; // floating point version of start & stop bins
	long curr_start_bin, curr_stop_bin; // start & stop bin for this effect
	float curr_amp;		// current amplitude scaling
	float curr_fxval;	// current effect value

protected:
	// block fx for each track
	auto_ptr<BlkFxProcess> blk_fx_process[N_FX_PARAM_SETS];	

	// Buzz fills in this structure
	Params params;

	// most recent params carried over for display purposes
	Params params_disp;

	// params vector to store trail of params
	ParamsVec params_vec;

	// Buzz machine pointer
	CMachine* this_machine;

	// GUI window
	BlkFxDialog* dialog;

};

#endif
