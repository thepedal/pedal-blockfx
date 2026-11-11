// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
#pragma once
#include <cplx.h>
class MIBlkFx;
struct MIBlkFxCommon { MIBlkFx* mi; void* dialog; MIBlkFxCommon():mi(0),dialog(0){}
  void sgramData(int, long, int, pcplxf){} void updateControls(){} void createDialog(){} };
template<class T> struct RefNew { T* p; RefNew():p(new T){} T* operator->(){return p;} T& operator*(){return *p;} };
