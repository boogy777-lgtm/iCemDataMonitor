//! Seeded System.Random compatibility, not a general or cryptographic RNG.
//! Algorithm port from dotnet/runtime v10.0.0, System/Random.CompatImpl.cs.
// Copyright (c) .NET Foundation and Contributors
// Licensed under the MIT License:
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
#[derive(Debug, Clone)]
pub struct SeededRandom {
    state: [i32; 56],
    next: usize,
    next_p: usize,
}
impl SeededRandom {
    pub fn new(seed: i32) -> Self {
        let subtraction = if seed == i32::MIN {
            i32::MAX
        } else {
            seed.abs()
        };
        let mut mj = 161803398 - subtraction;
        let mut state = [0_i32; 56];
        state[55] = mj;
        let mut mk = 1_i32;
        let mut ii = 0;
        for _ in 1..55 {
            ii += 21;
            if ii >= 55 {
                ii -= 55;
            }
            state[ii] = mk;
            mk = mj.wrapping_sub(mk);
            if mk < 0 {
                mk = mk.wrapping_add(i32::MAX);
            }
            mj = state[ii];
        }
        for _ in 1..5 {
            for i in 1..56 {
                let mut n = i + 30;
                if n >= 55 {
                    n -= 55;
                }
                state[i] = state[i].wrapping_sub(state[1 + n]);
                if state[i] < 0 {
                    state[i] = state[i].wrapping_add(i32::MAX);
                }
            }
        }
        Self {
            state,
            next: 0,
            next_p: 21,
        }
    }
    pub fn next_double(&mut self) -> f64 {
        self.next += 1;
        if self.next >= 56 {
            self.next = 1;
        }
        self.next_p += 1;
        if self.next_p >= 56 {
            self.next_p = 1;
        }
        let mut value = self.state[self.next].wrapping_sub(self.state[self.next_p]);
        if value == i32::MAX {
            value -= 1;
        }
        if value < 0 {
            value = value.wrapping_add(i32::MAX);
        }
        self.state[self.next] = value;
        f64::from(value) * (1.0 / f64::from(i32::MAX))
    }
}
#[derive(Debug, Clone)]
pub struct Context {
    pub elapsed_seconds: f64,
    pub dt: f64,
    pub tick: i64,
    pub random: SeededRandom,
}
impl Context {
    pub fn new(elapsed_seconds: f64, dt: f64, tick: i64, seed: i32) -> Self {
        let seed = seed
            .wrapping_mul(31)
            .wrapping_add((tick & 0x7fff_ffff) as i32);
        Self {
            elapsed_seconds,
            dt,
            tick,
            random: SeededRandom::new(seed),
        }
    }
}
