// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
#pragma once
#include <fftw3.h>
#define FFTW_REAL_TO_COMPLEX 0
#define FFTW_COMPLEX_TO_REAL 1
#define FFTW_ESTIMATE_ 0
struct rfftw_plan_s { int n; int dir; fftwf_plan p; float* in; float* out; };
typedef rfftw_plan_s* rfftw_plan;
inline rfftw_plan rfftw_create_plan(int n, int dir, int) {
  rfftw_plan r = new rfftw_plan_s; r->n=n; r->dir=dir;
  r->in=(float*)fftwf_malloc(sizeof(float)*n); r->out=(float*)fftwf_malloc(sizeof(float)*n);
  r->p=fftwf_plan_r2r_1d(n, r->in, r->out, dir==0?FFTW_R2HC:FFTW_HC2R, FFTW_ESTIMATE);
  return r; }
inline void rfftw_destroy_plan(rfftw_plan p) { fftwf_destroy_plan(p->p); delete p; }
inline void rfftw_one(rfftw_plan p, float* in, float* out) {
  memcpy(p->in, in, sizeof(float)*p->n); fftwf_execute(p->p); memcpy(out, p->out, sizeof(float)*p->n); }
