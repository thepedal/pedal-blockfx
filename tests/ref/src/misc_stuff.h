// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
/******************************************************************************
Miscellaneous stuff

History
	Date       Version    Programmer         Comments
	16/2/03    1.0        Darrell Tam		Created
******************************************************************************/

#ifndef _MISC_STUFF_H_
#define _MISC_STUFF_H_

#include <cplx.h>

using namespace std;

// stream "X" into std::string "S"
#define STRSTR(S, X) { ostringstream my_ostringstream; my_ostringstream << X; S = my_ostringstream.str(); }

// sizeof an array in elements
#define NUM_ELEMENTS(a) (sizeof(a)/sizeof(a[0]))

// re*re+im*im
inline float norm(float re, float im) { return norm(cplxf(re, im)); }

//------------------------------------------------------------------------------------------
//
// return linear interpolation at point <frac> between <out_min> &
// <out_max>, where <frac> is in the range 0 to 1
//
inline float lin_interp(float frac, float out_min, float out_max)
	{ return (1.0f-frac)*out_min + frac*out_max; }

//------------------------------------------------------------------------------------------
template<class A, class B, class C>
inline A limit_range(const A& in, const B& range_min, const C &range_max)
// limit range of "in" between "range_min" & "range_max"
{
	if(in < range_min) return range_min;
	if(in > range_max) return range_max;
	return in;
}

//------------------------------------------------------------------------------------------
template <class I, class X> I wrap(I i, const X& x)
// wrap index "i" inside of vector "x" (i.e. mod x.size())
{
	I n = x.size();
	i %= n;
	if(i < 0) i += n;
	return i;
}

//------------------------------------------------------------------------------------------
template <class A, class B> void setValue(A& dst, B src, bool& update)
// set "update" true if "dst" is changed
{
	if(dst == src) return;
	dst = src;
	update = true;
}

//------------------------------------------------------------------------------------------
template<class T> struct TChk
// hold current & next parameters
{
	T curr, next;

	void setBoth(const T& in) { curr = in; next = in; }

	// 
	explicit TChk(const T& in) { setBoth(in); }

	// default constructor
	TChk() {}

	// assign and return true if curr & next different
	bool update(void) { if(curr != next) { curr = next; return true; } return false; }

	// set <chg> and assign if curr & next different
	bool update(bool& chg) { bool t=update(); if(t) chg=true; return t; }

	// return true if curr & next different
	bool isChanged(void) const { return curr != next; }

	// assign to <next>
	void operator = (const T& next_) { next = next_; }

	// auto-cast <curr> to base type
	//operator const T& () const { return curr; }
	operator T () const { return curr; }

	// parens operator returns <curr>
	const T& operator () () const { return curr; }
};

//------------------------------------------------------------------------------------------
inline long prbs29(long x)
// pseudo random binary sequence (this one uses 29 bits and is 234880995 long - I chose this
// one at random - there are probably longer ones for the same complexity)
{
	return ((x<<1) ^ ((x>>22)&1) ^ ((x>>25)&1) ^ ((x>>28)&1));
}

//------------------------------------------------------------------------------------------
template <class T, int n> struct Array
// fixed length C type array, advantage is that assignment is allowed
{
	typedef T TYPE;
	// number of elements
	enum { SIZE = n };
	static int size() { return n; }

	// data store
	T data[n];

	// force deep-copy from a pointer
	void copy(const T* p) { *this = *(const Array*)p; }

	// array operator
	const T& operator[] (int i) const { return data[i]; }
	T& operator[] (int i) { return data[i]; }


	// cast to pointer
	operator T* () { return data; }
};

//------------------------------------------------------------------------------------------
template <class T, class BASE=T> struct new_auto_ptr : public auto_ptr<BASE>
{
	new_auto_ptr() : auto_ptr<BASE>(new T) { }
};

//------------------------------------------------------------------------------------------

//------------------------------------------------------------------------------------------
template <class T> size_t byte_sizeof(const std::vector<T>& v)
	{ return v.capacity()*sizeof(T); }

//------------------------------------------------------------------------------------------
class CallCreateDestroy
{
public:
	typedef void (*Fn)(void);
	Fn m_destroy;
	CallCreateDestroy(Fn create, Fn destroy=NULL) { (*create)(); m_destroy = destroy; }
	~CallCreateDestroy() { if(m_destroy) (*m_destroy)(); }
};

#endif
