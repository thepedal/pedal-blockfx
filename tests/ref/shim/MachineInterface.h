// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Minimal stand-in for the Buzz machine interface, written for this test harness
// (Pedal BlockFX, GPL-3.0-or-later). It declares only what the reference build of the
// original DT Block Effects DSP needs: the base class it derives from, the master info
// it reads, and the parameter-type, flag and work-mode constants. It is not a copy of
// Jeskola Buzz's MachineInterface.h and can't be used to build a real Buzz machine.
#pragma once
#include <stdio.h>
#include <assert.h>
#include <string.h>

typedef unsigned char byte;                       // value types used by the DSP code
typedef unsigned short word;
typedef unsigned long dword;

#define SWITCH_OFF  0                             // switch parameter values
#define SWITCH_ON   1
#define SWITCH_NO   255

enum { pt_note, pt_switch, pt_byte, pt_word };   // parameter types
#define MPF_STATE   2                             // parameter flag: has state
#define WM_NOIO     0                             // work modes
#define WM_READ     1
#define WM_WRITE    2
#define WM_READWRITE 3

class CMachine;

class CMachineDataInput
{
public:
    virtual ~CMachineDataInput() {}
    virtual void Read(void *pbuf, int const numbytes) = 0;
};

class CMasterInfo
{
public:
    int BeatsPerMin = 0, TicksPerBeat = 0, SamplesPerSec = 0, SamplesPerTick = 0, PosInTick = 0;
    float TicksPerSec = 0;
};

class CMICallbacks
{
public:
    virtual ~CMICallbacks() {}
    virtual char const *GetMachineName(CMachine *) = 0;
};

class CMachineInterface
{
public:
    virtual ~CMachineInterface() {}
    virtual void Init(CMachineDataInput * const) {}
    virtual void Tick() {}
    virtual bool Work(float *, int, int const) { return false; }
    virtual char const *DescribeValue(int const, int const) { return NULL; }
    virtual void SetNumTracks(int const) {}
    virtual void Command(int const) {}

    void *GlobalVals = NULL;
    void *TrackVals = NULL;
    int *AttrVals = NULL;
    CMasterInfo *pMasterInfo = NULL;
    CMICallbacks *pCB = NULL;
};
