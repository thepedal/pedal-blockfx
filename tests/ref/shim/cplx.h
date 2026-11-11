// SPDX-License-Identifier: GPL-3.0-or-later
// DT Block Effects, Copyright (C) 2003-2004 Darrell Tam. Released under the GNU GPL v3 or
// later with the author's permission (2026). Modified for the Pedal BlockFX test harness
// (include case, standard C++, two documented patches); see tests/README.md.
#ifndef _DT_CPLX_
#define _DT_CPLX_
/**************************************************************************************************
 Complex numbers class

 I wrote this class because std::complex didn't optimize very well (when I checked the
 asm code it had rubbish that wouldn't optimize away)

 History
 15/3/03	Darrell Tam		created

 TODO: lots of things - I only implemented what I needed

**************************************************************************************************/
#include <complex>

// adjacent complex
template <class A> struct cplx
{
	A r, i; // real, imaginary

	cplx(void) {}
	cplx(A _r, A _i) { r = _r; i = _i; }
	cplx operator () (A _r, A _i) { r = _r; i = _i; return *this; }

	cplx(std::complex<A> a) { r = a.real(); i = a.imag(); }
	cplx(A _r) { r = _r; i = 0.0f; }

	float real() const { return r; }
	float imag() const { return i; }

	A& real(void) { return r; }
	A& imag(void) { return i; }

	operator std::complex<A> (void) { return std::complex<A>(r, i); }

};

// dereferrenced non-adjacent complex pointer
template <class A> struct dpcplx
{
	A *r, *i;

	dpcplx() {}
	dpcplx(A *_r, A *_i) { r = _r; i = _i; }

	dpcplx operator () (A *_r, A *_i) { r = _r; i = _i; return *this; }
	
	float real() const { return *r; }
	float imag() const { return *i; }

	float& real() { return *r; }
	float& imag() { return *i; }

	cplx<A> operator= (cplx<A> x) { *r = x.r; *i = x.i; return x; }
	operator std::complex<A> (void) { return std::complex<A>(*r, *i); }
	operator cplx<A> (void) { return cplx<A>(*r, *i); }
};


// awful stuff... and this is incomplete
template<class T> inline cplx<T> operator*(cplx<T> a, cplx<T> b) { cplx<T> r; r.r=a.real()*b.real()-a.imag()*b.imag(); r.i=a.real()*b.imag()+a.imag()*b.real(); return r; } 
template<class T> inline cplx<T> operator*(dpcplx<T> a, cplx<T> b) { cplx<T> r; r.r=a.real()*b.real()-a.imag()*b.imag(); r.i=a.real()*b.imag()+a.imag()*b.real(); return r; } 
template<class T> inline cplx<T> operator*(cplx<T> a, dpcplx<T> b) { cplx<T> r; r.r=a.real()*b.real()-a.imag()*b.imag(); r.i=a.real()*b.imag()+a.imag()*b.real(); return r; } 
template<class T> inline cplx<T> operator*(dpcplx<T> a, dpcplx<T> b) { cplx<T> r; r.r=a.real()*b.real()-a.imag()*b.imag(); r.i=a.real()*b.imag()+a.imag()*b.real(); return r; } 
template<class T> inline cplx<T> operator*(T a, cplx<T> b) { cplx<T> r; r.r=a*b.real(); r.i=a*b.imag(); return r; } 
template<class T> inline cplx<T> operator*(T a, dpcplx<T> b) { cplx<T> r; r.r=a*b.real(); r.i=a*b.imag(); return r; } 
template<class T> inline cplx<T> operator*(cplx<T> a, T b) { cplx<T> r; r.r=a.real()*b; r.i=a.imag()*b; return r; } 
template<class T> inline cplx<T> operator*(dpcplx<T> a, T b) { cplx<T> r; r.r=a.real()*b; r.i=a.imag()*b; return r; } 
template<class T> inline cplx<T> operator+(cplx<T> a, cplx<T> b) { cplx<T> r; r.r=a.real()+b.real(); r.i=a.imag()+b.imag(); return r; } 
template<class T> inline cplx<T> operator+(dpcplx<T> a, cplx<T> b) { cplx<T> r; r.r=a.real()+b.real(); r.i=a.imag()+b.imag(); return r; } 
template<class T> inline cplx<T> operator+(cplx<T> a, dpcplx<T> b) { cplx<T> r; r.r=a.real()+b.real(); r.i=a.imag()+b.imag(); return r; } 
template<class T> inline cplx<T> operator+(dpcplx<T> a, dpcplx<T> b) { cplx<T> r; r.r=a.real()+b.real(); r.i=a.imag()+b.imag(); return r; } 
template<class T> inline cplx<T> operator-(cplx<T> a, cplx<T> b) { cplx<T> r; r.r=a.real()-b.real(); r.i=a.imag()-b.imag(); return r; } 
template<class T> inline cplx<T> operator-(dpcplx<T> a, cplx<T> b) { cplx<T> r; r.r=a.real()-b.real(); r.i=a.imag()-b.imag(); return r; } 
template<class T> inline cplx<T> operator-(cplx<T> a, dpcplx<T> b) { cplx<T> r; r.r=a.real()-b.real(); r.i=a.imag()-b.imag(); return r; } 
template<class T> inline cplx<T> operator-(dpcplx<T> a, dpcplx<T> b) { cplx<T> r; r.r=a.real()-b.real(); r.i=a.imag()-b.imag(); return r; } 
template<class T> inline cplx<T> operator+(T a, cplx<T> b) { cplx<T> r; r.r=a+b.real(); r.i=a.imag(); return r; } 
template<class T> inline cplx<T> operator+(T a, dpcplx<T> b) { cplx<T> r; r.r=a+b.real(); r.i=a.imag(); return r; } 
template<class T> inline cplx<T> operator+(cplx<T> a, T b) { cplx<T> r; r.r=a.real()+b; r.i=a.imag(); return r; } 
template<class T> inline cplx<T> operator+(dpcplx<T> a, T b) { cplx<T> r; r.r=a.real()+b; r.i=a.imag(); return r; } 
template<class T> inline cplx<T> operator-(T a, cplx<T> b) { cplx<T> r; r.r=a-b.real(); r.i=-b.imag(); return r; } 
template<class T> inline cplx<T> operator-(T a, dpcplx<T> b) { cplx<T> r; r.r=a-b.real(); r.i=-b.imag(); return r; } 
template<class T> inline cplx<T> operator-(cplx<T> a, T b) { cplx<T> r; r.r=a.real()-b; r.i=a.imag(); return r; } 
template<class T> inline cplx<T> operator-(dpcplx<T> a, T b) { cplx<T> r; r.r=a.real()-b; r.i=a.imag(); return r; } 
template<class T> inline cplx<T> operator-(cplx<T> a) { cplx<T> r; r.r=-a.real(); r.i=-a.imag(); return r; }
template<class T> inline cplx<T> operator-(dpcplx<T> a) { cplx<T> r; r.r=-a.real(); r.i=-a.imag(); return r; }
template<class T> inline T norm(cplx<T> a) { return a.real()*a.real()+a.imag()*a.imag(); } 
template<class T> inline T norm(dpcplx<T> a) { return a.real()*a.real()+a.imag()*a.imag(); } 


// non-adjacent real & complex pointer (e.g. output from fftw)
template <class A> struct pcplx : public dpcplx<A>
{
	pcplx() {}
	pcplx(A *_r, A *_i) { this->r = _r; this->i = _i; }
	pcplx(A *x, long len, long offs=0) { this->r = x+offs; this->i = x+len-offs; }

	pcplx& operator() (A *x, long len, long offs) { this->r=x+offs; this->i=x+len-offs; return *this; }

	// postfix
	pcplx operator++ (int) { pcplx x(this->r, this->i); this->r++; this->i--; return x; }	
	pcplx operator-- (int) { pcplx x(this->r, this->i); this->r--; this->i++; return x; }	

	// prefix
	pcplx& operator++ (void) { ++this->r; ++this->i; return *this; }
	pcplx& operator-- (void) { --this->r; --this->i; return *this; }

	// array
	cplx<A> operator [] (int a) const { return cplx<A>(this->r[a], this->i[-a]); }
	dpcplx<A> operator [] (int a) { return dpcplx<A>(this->r+a, this->i-a); }

	// dereference
	cplx<A> operator * () const { return cplx<A>(*this->r, *this->i); }
	dpcplx<A> operator * () { return dpcplx<A>(this->r, this->i); }
};


typedef cplx<float> cplxf;
typedef pcplx<float> pcplxf;
const cplxf If(0.0f, 1.0f);

//------------------------------------------------------------------------------------------
struct pcplxf_range : public pcplxf
{
	float *e; // end, real
	pcplxf_range(float*x, long fft_len, long start, long end) :
		pcplxf(x, fft_len, start), e(x+end) {}
};


#endif
