// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
#pragma once
#ifndef _FREQ_TO_NOTE_H_
#define _FREQ_TO_NOTE_H_

#define A4_FREQ 440.000031f

// string format of notes: <note>[#]:<cents> to Hz
// example: c#4:-45

int HzToNote(char* out_txt, float freq);
float NoteToHz(const char* txt);


#endif
