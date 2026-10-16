var __create = Object.create;
var __getProtoOf = Object.getPrototypeOf;
var __defProp = Object.defineProperty;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __hasOwnProp = Object.prototype.hasOwnProperty;
function __accessProp(key) {
  return this[key];
}
var __toESMCache_node;
var __toESMCache_esm;
var __toESM = (mod, isNodeMode, target) => {
  var canCache = mod != null && typeof mod === "object";
  if (canCache) {
    var cache = isNodeMode ? __toESMCache_node ??= new WeakMap : __toESMCache_esm ??= new WeakMap;
    var cached = cache.get(mod);
    if (cached)
      return cached;
  }
  target = mod != null ? __create(__getProtoOf(mod)) : {};
  const to = isNodeMode || !mod || !mod.__esModule ? __defProp(target, "default", { value: mod, enumerable: true }) : target;
  if (mod && typeof mod === "object" || typeof mod === "function") {
    for (let key of __getOwnPropNames(mod))
      if (!__hasOwnProp.call(to, key))
        __defProp(to, key, {
          get: __accessProp.bind(mod, key),
          enumerable: true
        });
  }
  if (canCache)
    cache.set(mod, to);
  return to;
};
var __commonJS = (cb, mod) => () => (mod || cb((mod = { exports: {} }).exports, mod), mod.exports);

// node_modules/grpc-web/index.js
var require_grpc_web = __commonJS(function(exports, module) {
  var n;
  function aa(a) {
    var b = 0;
    return function() {
      return b < a.length ? { done: false, value: a[b++] } : { done: true };
    };
  }
  var ba = typeof Object.defineProperties == "function" ? Object.defineProperty : function(a, b, c) {
    a != Array.prototype && a != Object.prototype && (a[b] = c.value);
  };
  function ca(a) {
    a = [typeof window == "object" && window, typeof self == "object" && self, typeof global == "object" && global, a];
    for (var b = 0;b < a.length; ++b) {
      var c = a[b];
      if (c && c.Math == Math)
        return c;
    }
    throw Error("Cannot find global object");
  }
  var r = ca(exports);
  function t() {
    t = function() {};
    r.Symbol || (r.Symbol = da);
  }
  function ea(a, b) {
    this.a = a;
    ba(this, "description", { configurable: true, writable: true, value: b });
  }
  ea.prototype.toString = function() {
    return this.a;
  };
  var da = function() {
    function a(c) {
      if (this instanceof a)
        throw new TypeError("Symbol is not a constructor");
      return new ea("jscomp_symbol_" + (c || "") + "_" + b++, c);
    }
    var b = 0;
    return a;
  }();
  function u() {
    t();
    var a = r.Symbol.iterator;
    a || (a = r.Symbol.iterator = r.Symbol("Symbol.iterator"));
    typeof Array.prototype[a] != "function" && ba(Array.prototype, a, { configurable: true, writable: true, value: function() {
      return fa(aa(this));
    } });
    u = function() {};
  }
  function fa(a) {
    u();
    a = { next: a };
    a[r.Symbol.iterator] = function() {
      return this;
    };
    return a;
  }
  function ha(a) {
    var b = typeof Symbol != "undefined" && Symbol.iterator && a[Symbol.iterator];
    return b ? b.call(a) : { next: aa(a) };
  }
  var ia = typeof Object.create == "function" ? Object.create : function(a) {
    function b() {}
    b.prototype = a;
    return new b;
  };
  var ja;
  if (typeof Object.setPrototypeOf == "function")
    ja = Object.setPrototypeOf;
  else {
    a: {
      la = { V: true }, ma = {};
      try {
        ma.__proto__ = la;
        ka = ma.V;
        break a;
      } catch (a) {}
      ka = false;
    }
    ja = ka ? function(a, b) {
      a.__proto__ = b;
      if (a.__proto__ !== b)
        throw new TypeError(a + " is not extensible");
      return a;
    } : null;
  }
  var ka;
  var la;
  var ma;
  var na = ja;
  function oa(a, b) {
    a.prototype = ia(b.prototype);
    a.prototype.constructor = a;
    if (na)
      na(a, b);
    else
      for (var c in b)
        if (c != "prototype")
          if (Object.defineProperties) {
            var d = Object.getOwnPropertyDescriptor(b, c);
            d && Object.defineProperty(a, c, d);
          } else
            a[c] = b[c];
    a.O = b.prototype;
  }
  function pa(a, b) {
    u();
    a instanceof String && (a += "");
    var c = 0, d = { next: function() {
      if (c < a.length) {
        var f = c++;
        return { value: b(f, a[f]), done: false };
      }
      d.next = function() {
        return { done: true, value: undefined };
      };
      return d.next();
    } };
    d[Symbol.iterator] = function() {
      return d;
    };
    return d;
  }
  function v(a, b) {
    if (b) {
      var c = r;
      a = a.split(".");
      for (var d = 0;d < a.length - 1; d++) {
        var f = a[d];
        f in c || (c[f] = {});
        c = c[f];
      }
      a = a[a.length - 1];
      d = c[a];
      b = b(d);
      b != d && b != null && ba(c, a, { configurable: true, writable: true, value: b });
    }
  }
  v("Array.prototype.keys", function(a) {
    return a ? a : function() {
      return pa(this, function(b) {
        return b;
      });
    };
  });
  v("Array.prototype.find", function(a) {
    return a ? a : function(b, c) {
      a: {
        var d = this;
        d instanceof String && (d = String(d));
        for (var f = d.length, g = 0;g < f; g++) {
          var e = d[g];
          if (b.call(c, e, g, d)) {
            b = e;
            break a;
          }
        }
        b = undefined;
      }
      return b;
    };
  });
  v("Object.is", function(a) {
    return a ? a : function(b, c) {
      return b === c ? b !== 0 || 1 / b === 1 / c : b !== b && c !== c;
    };
  });
  v("Array.prototype.includes", function(a) {
    return a ? a : function(b, c) {
      var d = this;
      d instanceof String && (d = String(d));
      var f = d.length;
      c = c || 0;
      for (0 > c && (c = Math.max(c + f, 0));c < f; c++) {
        var g = d[c];
        if (g === b || Object.is(g, b))
          return true;
      }
      return false;
    };
  });
  v("Promise", function(a) {
    function b(e) {
      this.b = 0;
      this.c = undefined;
      this.a = [];
      var h = this.f();
      try {
        e(h.resolve, h.reject);
      } catch (k) {
        h.reject(k);
      }
    }
    function c() {
      this.a = null;
    }
    function d(e) {
      return e instanceof b ? e : new b(function(h) {
        h(e);
      });
    }
    if (a)
      return a;
    c.prototype.b = function(e) {
      if (this.a == null) {
        this.a = [];
        var h = this;
        this.c(function() {
          h.g();
        });
      }
      this.a.push(e);
    };
    var f = r.setTimeout;
    c.prototype.c = function(e) {
      f(e, 0);
    };
    c.prototype.g = function() {
      for (;this.a && this.a.length; ) {
        var e = this.a;
        this.a = [];
        for (var h = 0;h < e.length; ++h) {
          var k = e[h];
          e[h] = null;
          try {
            k();
          } catch (l) {
            this.f(l);
          }
        }
      }
      this.a = null;
    };
    c.prototype.f = function(e) {
      this.c(function() {
        throw e;
      });
    };
    b.prototype.f = function() {
      function e(l) {
        return function(m) {
          k || (k = true, l.call(h, m));
        };
      }
      var h = this, k = false;
      return { resolve: e(this.s), reject: e(this.g) };
    };
    b.prototype.s = function(e) {
      if (e === this)
        this.g(new TypeError("A Promise cannot resolve to itself"));
      else if (e instanceof b)
        this.v(e);
      else {
        a:
          switch (typeof e) {
            case "object":
              var h = e != null;
              break a;
            case "function":
              h = true;
              break a;
            default:
              h = false;
          }
        h ? this.m(e) : this.h(e);
      }
    };
    b.prototype.m = function(e) {
      var h = undefined;
      try {
        h = e.then;
      } catch (k) {
        this.g(k);
        return;
      }
      typeof h == "function" ? this.w(h, e) : this.h(e);
    };
    b.prototype.g = function(e) {
      this.i(2, e);
    };
    b.prototype.h = function(e) {
      this.i(1, e);
    };
    b.prototype.i = function(e, h) {
      if (this.b != 0)
        throw Error("Cannot settle(" + e + ", " + h + "): Promise already settled in state" + this.b);
      this.b = e;
      this.c = h;
      this.l();
    };
    b.prototype.l = function() {
      if (this.a != null) {
        for (var e = 0;e < this.a.length; ++e)
          g.b(this.a[e]);
        this.a = null;
      }
    };
    var g = new c;
    b.prototype.v = function(e) {
      var h = this.f();
      e.F(h.resolve, h.reject);
    };
    b.prototype.w = function(e, h) {
      var k = this.f();
      try {
        e.call(h, k.resolve, k.reject);
      } catch (l) {
        k.reject(l);
      }
    };
    b.prototype.then = function(e, h) {
      function k(q, w) {
        return typeof q == "function" ? function(A) {
          try {
            l(q(A));
          } catch (L) {
            m(L);
          }
        } : w;
      }
      var l, m, p = new b(function(q, w) {
        l = q;
        m = w;
      });
      this.F(k(e, l), k(h, m));
      return p;
    };
    b.prototype.catch = function(e) {
      return this.then(undefined, e);
    };
    b.prototype.F = function(e, h) {
      function k() {
        switch (l.b) {
          case 1:
            e(l.c);
            break;
          case 2:
            h(l.c);
            break;
          default:
            throw Error("Unexpected state: " + l.b);
        }
      }
      var l = this;
      this.a == null ? g.b(k) : this.a.push(k);
    };
    b.resolve = d;
    b.reject = function(e) {
      return new b(function(h, k) {
        k(e);
      });
    };
    b.race = function(e) {
      return new b(function(h, k) {
        for (var l = ha(e), m = l.next();!m.done; m = l.next())
          d(m.value).F(h, k);
      });
    };
    b.all = function(e) {
      var h = ha(e), k = h.next();
      return k.done ? d([]) : new b(function(l, m) {
        function p(A) {
          return function(L) {
            q[A] = L;
            w--;
            w == 0 && l(q);
          };
        }
        var q = [], w = 0;
        do
          q.push(undefined), w++, d(k.value).F(p(q.length - 1), m), k = h.next();
        while (!k.done);
      });
    };
    return b;
  });
  var qa = qa || {};
  var x = exports || self;
  function y(a, b) {
    a = a.split(".");
    b = b || x;
    for (var c = 0;c < a.length; c++)
      if (b = b[a[c]], b == null)
        return null;
    return b;
  }
  function ra() {}
  function sa(a) {
    var b = typeof a;
    return b == "object" && a != null || b == "function";
  }
  var ta = "closure_uid_" + (1e9 * Math.random() >>> 0);
  var ua = 0;
  function va(a, b, c) {
    return a.call.apply(a.bind, arguments);
  }
  function wa(a, b, c) {
    if (!a)
      throw Error();
    if (2 < arguments.length) {
      var d = Array.prototype.slice.call(arguments, 2);
      return function() {
        var f = Array.prototype.slice.call(arguments);
        Array.prototype.unshift.apply(f, d);
        return a.apply(b, f);
      };
    }
    return function() {
      return a.apply(b, arguments);
    };
  }
  function z(a, b, c) {
    Function.prototype.bind && Function.prototype.bind.toString().indexOf("native code") != -1 ? z = va : z = wa;
    return z.apply(null, arguments);
  }
  function B(a, b) {
    function c() {}
    c.prototype = b.prototype;
    a.O = b.prototype;
    a.prototype = new c;
    a.prototype.constructor = a;
  }
  function xa(a) {
    this.a = a || {};
  }
  xa.prototype.get = function(a) {
    return this.a[a];
  };
  xa.prototype.G = function() {
    return Object.keys(this.a);
  };
  function C(a, b, c, d) {
    this.f = a;
    this.c = b;
    this.b = c;
    this.a = d;
  }
  C.prototype.getRequestMessage = function() {
    return this.f;
  };
  C.prototype.getMethodDescriptor = function() {
    return this.c;
  };
  C.prototype.getMetadata = function() {
    return this.b;
  };
  C.prototype.getCallOptions = function() {
    return this.a;
  };
  function D(a, b, c, d) {
    c = c === undefined ? {} : c;
    this.c = a;
    this.a = c;
    this.b = b;
    this.f = d === undefined ? null : d;
  }
  D.prototype.getResponseMessage = function() {
    return this.c;
  };
  D.prototype.getMetadata = function() {
    return this.a;
  };
  D.prototype.getMethodDescriptor = function() {
    return this.b;
  };
  D.prototype.getStatus = function() {
    return this.f;
  };
  function ya(a, b, c, d, f, g) {
    this.name = a;
    this.a = f;
    this.b = g;
  }
  function za(a, b, c) {
    c = c === undefined ? {} : c;
    var d = d === undefined ? new xa : d;
    return new C(b, a, c, d);
  }
  ya.prototype.getName = function() {
    return this.name;
  };
  ya.prototype.getName = ya.prototype.getName;
  function Aa(a) {
    switch (a) {
      case 200:
        return 0;
      case 400:
        return 3;
      case 401:
        return 16;
      case 403:
        return 7;
      case 404:
        return 5;
      case 409:
        return 10;
      case 412:
        return 9;
      case 429:
        return 8;
      case 499:
        return 1;
      case 500:
        return 2;
      case 501:
        return 12;
      case 503:
        return 14;
      case 504:
        return 4;
      default:
        return 2;
    }
  }
  function Ba(a) {
    switch (a) {
      case 0:
        return "OK";
      case 1:
        return "CANCELLED";
      case 2:
        return "UNKNOWN";
      case 3:
        return "INVALID_ARGUMENT";
      case 4:
        return "DEADLINE_EXCEEDED";
      case 5:
        return "NOT_FOUND";
      case 6:
        return "ALREADY_EXISTS";
      case 7:
        return "PERMISSION_DENIED";
      case 16:
        return "UNAUTHENTICATED";
      case 8:
        return "RESOURCE_EXHAUSTED";
      case 9:
        return "FAILED_PRECONDITION";
      case 10:
        return "ABORTED";
      case 11:
        return "OUT_OF_RANGE";
      case 12:
        return "UNIMPLEMENTED";
      case 13:
        return "INTERNAL";
      case 14:
        return "UNAVAILABLE";
      case 15:
        return "DATA_LOSS";
      default:
        return "";
    }
  }
  function E(a, b, c) {
    c = c === undefined ? {} : c;
    b = Error.call(this, b);
    this.message = b.message;
    "stack" in b && (this.stack = b.stack);
    this.code = a;
    this.metadata = c;
  }
  oa(E, Error);
  E.prototype.toString = function() {
    var a = "RpcError(" + (Ba(this.code) || String(this.code)) + ")";
    this.message && (a += ": " + this.message);
    return a;
  };
  E.prototype.name = "RpcError";
  function Ca(a) {
    this.a = a;
  }
  Ca.prototype.on = function(a, b) {
    return a == "data" || a == "error" ? this : this.a.on(a, b);
  };
  Ca.prototype.removeListener = function(a, b) {
    return this.a.removeListener(a, b);
  };
  Ca.prototype.cancel = function() {
    this.a.cancel();
  };
  function Da(a) {
    switch (a) {
      case 0:
        return "No Error";
      case 1:
        return "Access denied to content document";
      case 2:
        return "File not found";
      case 3:
        return "Firefox silently errored";
      case 4:
        return "Application custom error";
      case 5:
        return "An exception occurred";
      case 6:
        return "Http response at 400 or 500 level";
      case 7:
        return "Request was aborted";
      case 8:
        return "Request timed out";
      case 9:
        return "The resource is not available offline";
      default:
        return "Unrecognized error code";
    }
  }
  function F(a) {
    if (Error.captureStackTrace)
      Error.captureStackTrace(this, F);
    else {
      var b = Error().stack;
      b && (this.stack = b);
    }
    a && (this.message = String(a));
  }
  B(F, Error);
  F.prototype.name = "CustomError";
  function Ea(a, b) {
    a = a.split("%s");
    for (var c = "", d = a.length - 1, f = 0;f < d; f++)
      c += a[f] + (f < b.length ? b[f] : "%s");
    F.call(this, c + a[d]);
  }
  B(Ea, F);
  Ea.prototype.name = "AssertionError";
  function Fa(a, b) {
    throw new Ea("Failure" + (a ? ": " + a : ""), Array.prototype.slice.call(arguments, 1));
  }
  function Ga() {
    this.l = null;
    this.i = [];
    this.m = 0;
    this.b = Ha;
    this.f = this.a = this.h = 0;
    this.c = null;
    this.g = 0;
  }
  function Ia(a, b) {
    function c(l) {
      l == Ja ? e.h = l : l == G ? e.h = l : Ka(e, h, k, "invalid frame byte");
      e.b = La;
      e.a = 0;
      e.f = 0;
    }
    function d(l) {
      e.f++;
      e.a = (e.a << 8) + l;
      e.f == 4 && (e.b = Ma, e.g = 0, typeof Uint8Array !== "undefined" ? e.c = new Uint8Array(e.a) : e.c = Array(e.a), e.a == 0 && g());
    }
    function f(l) {
      e.c[e.g++] = l;
      e.g == e.a && g();
    }
    function g() {
      var l = {};
      l[e.h] = e.c;
      e.i.push(l);
      e.b = Ha;
    }
    var e = a, h, k = 0;
    for (b instanceof Uint8Array || b instanceof Array ? h = b : h = new Uint8Array(b);k < h.length; ) {
      switch (e.b) {
        case Na:
          Ka(e, h, k, "stream already broken");
          break;
        case Ha:
          c(h[k]);
          break;
        case La:
          d(h[k]);
          break;
        case Ma:
          f(h[k]);
          break;
        default:
          throw Error("unexpected parser state: " + e.b);
      }
      e.m++;
      k++;
    }
    a = e.i;
    e.i = [];
    return 0 < a.length ? a : null;
  }
  var Ha = 0;
  var La = 1;
  var Ma = 2;
  var Na = 3;
  var Ja = 0;
  var G = 128;
  function Ka(a, b, c, d) {
    a.b = Na;
    a.l = "The stream is broken @" + a.m + "/" + c + ". Error: " + d + `. With input:
` + b;
    throw Error(a.l);
  }
  var Oa = Array.prototype.indexOf ? function(a, b) {
    return Array.prototype.indexOf.call(a, b, undefined);
  } : function(a, b) {
    if (typeof a === "string")
      return typeof b !== "string" || b.length != 1 ? -1 : a.indexOf(b, 0);
    for (var c = 0;c < a.length; c++)
      if (c in a && a[c] === b)
        return c;
    return -1;
  };
  var Pa = String.prototype.trim ? function(a) {
    return a.trim();
  } : function(a) {
    return /^[\s\xa0]*([\s\S]*?)[\s\xa0]*$/.exec(a)[1];
  };
  function H(a, b) {
    return a.indexOf(b) != -1;
  }
  function Qa(a, b) {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  var I;
  a: {
    Ra = x.navigator;
    if (Ra) {
      Sa = Ra.userAgent;
      if (Sa) {
        I = Sa;
        break a;
      }
    }
    I = "";
  }
  var Ra;
  var Sa;
  function Ta(a, b) {
    for (var c in a)
      b.call(undefined, a[c], c, a);
  }
  function Ua(a, b) {
    var c = {}, d;
    for (d in a)
      c[d] = b.call(undefined, a[d], d, a);
    return c;
  }
  var Va = "constructor hasOwnProperty isPrototypeOf propertyIsEnumerable toLocaleString toString valueOf".split(" ");
  function Wa(a, b) {
    for (var c, d, f = 1;f < arguments.length; f++) {
      d = arguments[f];
      for (c in d)
        a[c] = d[c];
      for (var g = 0;g < Va.length; g++)
        c = Va[g], Object.prototype.hasOwnProperty.call(d, c) && (a[c] = d[c]);
    }
  }
  function Xa(a) {
    var b = 1;
    a = a.split(":");
    for (var c = [];0 < b && a.length; )
      c.push(a.shift()), b--;
    a.length && c.push(a.join(":"));
    return c;
  }
  function Ya(a) {
    Ya[" "](a);
    return a;
  }
  Ya[" "] = ra;
  function Za(a) {
    var b = $a;
    return Object.prototype.hasOwnProperty.call(b, 9) ? b[9] : b[9] = a(9);
  }
  var ab = H(I, "Opera");
  var bb = H(I, "Trident") || H(I, "MSIE");
  var cb = H(I, "Edge");
  var db = H(I, "Gecko") && !(H(I.toLowerCase(), "webkit") && !H(I, "Edge")) && !(H(I, "Trident") || H(I, "MSIE")) && !H(I, "Edge");
  var eb = H(I.toLowerCase(), "webkit") && !H(I, "Edge");
  var fb;
  a: {
    gb = "", hb = function() {
      var a = I;
      if (db)
        return /rv:([^\);]+)(\)|;)/.exec(a);
      if (cb)
        return /Edge\/([\d\.]+)/.exec(a);
      if (bb)
        return /\b(?:MSIE|rv)[: ]([^\);]+)(\)|;)/.exec(a);
      if (eb)
        return /WebKit\/(\S+)/.exec(a);
      if (ab)
        return /(?:Version)[ \/]?(\S+)/.exec(a);
    }();
    hb && (gb = hb ? hb[1] : "");
    if (bb) {
      jb = x.document;
      ib = jb ? jb.documentMode : undefined;
      if (ib != null && ib > parseFloat(gb)) {
        fb = String(ib);
        break a;
      }
    }
    fb = gb;
  }
  var gb;
  var hb;
  var ib;
  var jb;
  var $a = {};
  function kb() {
    return Za(function() {
      for (var a = 0, b = Pa(String(fb)).split("."), c = Pa("9").split("."), d = Math.max(b.length, c.length), f = 0;a == 0 && f < d; f++) {
        var g = b[f] || "", e = c[f] || "";
        do {
          g = /(\d*)(\D*)(.*)/.exec(g) || ["", "", "", ""];
          e = /(\d*)(\D*)(.*)/.exec(e) || ["", "", "", ""];
          if (g[0].length == 0 && e[0].length == 0)
            break;
          a = Qa(g[1].length == 0 ? 0 : parseInt(g[1], 10), e[1].length == 0 ? 0 : parseInt(e[1], 10)) || Qa(g[2].length == 0, e[2].length == 0) || Qa(g[2], e[2]);
          g = g[3];
          e = e[3];
        } while (a == 0);
      }
      return 0 <= a;
    });
  }
  function lb() {
    mb != 0 && (Object.prototype.hasOwnProperty.call(this, ta) && this[ta] || (this[ta] = ++ua));
    this.K = this.K;
  }
  var mb = 0;
  lb.prototype.K = false;
  var nb = Object.freeze || function(a) {
    return a;
  };
  function J(a, b) {
    this.type = a;
    this.a = this.target = b;
    this.defaultPrevented = false;
  }
  J.prototype.b = function() {
    this.defaultPrevented = true;
  };
  var ob = function() {
    if (!x.addEventListener || !Object.defineProperty)
      return false;
    var a = false, b = Object.defineProperty({}, "passive", { get: function() {
      a = true;
    } });
    try {
      x.addEventListener("test", ra, b), x.removeEventListener("test", ra, b);
    } catch (c) {}
    return a;
  }();
  function K(a, b) {
    J.call(this, a ? a.type : "");
    this.relatedTarget = this.a = this.target = null;
    this.button = this.screenY = this.screenX = this.clientY = this.clientX = 0;
    this.key = "";
    this.metaKey = this.shiftKey = this.altKey = this.ctrlKey = false;
    this.pointerId = 0;
    this.pointerType = "";
    this.c = null;
    if (a) {
      var c = this.type = a.type, d = a.changedTouches && a.changedTouches.length ? a.changedTouches[0] : null;
      this.target = a.target || a.srcElement;
      this.a = b;
      if (b = a.relatedTarget) {
        if (db) {
          a: {
            try {
              Ya(b.nodeName);
              var f = true;
              break a;
            } catch (g) {}
            f = false;
          }
          f || (b = null);
        }
      } else
        c == "mouseover" ? b = a.fromElement : c == "mouseout" && (b = a.toElement);
      this.relatedTarget = b;
      d ? (this.clientX = d.clientX !== undefined ? d.clientX : d.pageX, this.clientY = d.clientY !== undefined ? d.clientY : d.pageY, this.screenX = d.screenX || 0, this.screenY = d.screenY || 0) : (this.clientX = a.clientX !== undefined ? a.clientX : a.pageX, this.clientY = a.clientY !== undefined ? a.clientY : a.pageY, this.screenX = a.screenX || 0, this.screenY = a.screenY || 0);
      this.button = a.button;
      this.key = a.key || "";
      this.ctrlKey = a.ctrlKey;
      this.altKey = a.altKey;
      this.shiftKey = a.shiftKey;
      this.metaKey = a.metaKey;
      this.pointerId = a.pointerId || 0;
      this.pointerType = typeof a.pointerType === "string" ? a.pointerType : pb[a.pointerType] || "";
      this.c = a;
      a.defaultPrevented && K.O.b.call(this);
    }
  }
  B(K, J);
  var pb = nb({ 2: "touch", 3: "pen", 4: "mouse" });
  K.prototype.b = function() {
    K.O.b.call(this);
    var a = this.c;
    a.preventDefault ? a.preventDefault() : a.returnValue = false;
  };
  var M = "closure_listenable_" + (1e6 * Math.random() | 0);
  var qb = 0;
  function rb(a, b, c, d, f) {
    this.listener = a;
    this.proxy = null;
    this.src = b;
    this.type = c;
    this.capture = !!d;
    this.H = f;
    this.key = ++qb;
    this.A = this.D = false;
  }
  function sb(a) {
    a.A = true;
    a.listener = null;
    a.proxy = null;
    a.src = null;
    a.H = null;
  }
  function tb(a) {
    this.src = a;
    this.a = {};
    this.b = 0;
  }
  tb.prototype.add = function(a, b, c, d, f) {
    var g = a.toString();
    a = this.a[g];
    a || (a = this.a[g] = [], this.b++);
    var e = ub(a, b, d, f);
    -1 < e ? (b = a[e], c || (b.D = false)) : (b = new rb(b, this.src, g, !!d, f), b.D = c, a.push(b));
    return b;
  };
  tb.prototype.remove = function(a, b, c, d) {
    a = a.toString();
    if (!(a in this.a))
      return false;
    var f = this.a[a];
    b = ub(f, b, c, d);
    return -1 < b ? (sb(f[b]), Array.prototype.splice.call(f, b, 1), f.length == 0 && (delete this.a[a], this.b--), true) : false;
  };
  function vb(a, b) {
    var c = b.type;
    if (c in a.a) {
      var d = a.a[c], f = Oa(d, b), g;
      (g = 0 <= f) && Array.prototype.splice.call(d, f, 1);
      g && (sb(b), a.a[c].length == 0 && (delete a.a[c], a.b--));
    }
  }
  function ub(a, b, c, d) {
    for (var f = 0;f < a.length; ++f) {
      var g = a[f];
      if (!g.A && g.listener == b && g.capture == !!c && g.H == d)
        return f;
    }
    return -1;
  }
  var wb = "closure_lm_" + (1e6 * Math.random() | 0);
  var xb = {};
  var yb = 0;
  function zb(a, b, c, d, f) {
    if (d && d.once)
      Ab(a, b, c, d, f);
    else if (Array.isArray(b))
      for (var g = 0;g < b.length; g++)
        zb(a, b[g], c, d, f);
    else
      c = Bb(c), a && a[M] ? a.f.add(String(b), c, false, sa(d) ? !!d.capture : !!d, f) : Cb(a, b, c, false, d, f);
  }
  function Cb(a, b, c, d, f, g) {
    if (!b)
      throw Error("Invalid event type");
    var e = sa(f) ? !!f.capture : !!f, h = Db(a);
    h || (a[wb] = h = new tb(a));
    c = h.add(b, c, d, e, g);
    if (!c.proxy) {
      d = Eb();
      c.proxy = d;
      d.src = a;
      d.listener = c;
      if (a.addEventListener)
        ob || (f = e), f === undefined && (f = false), a.addEventListener(b.toString(), d, f);
      else if (a.attachEvent)
        a.attachEvent(Fb(b.toString()), d);
      else if (a.addListener && a.removeListener)
        a.addListener(d);
      else
        throw Error("addEventListener and attachEvent are unavailable.");
      yb++;
    }
  }
  function Eb() {
    function a(c) {
      return b.call(a.src, a.listener, c);
    }
    var b = Gb;
    return a;
  }
  function Ab(a, b, c, d, f) {
    if (Array.isArray(b))
      for (var g = 0;g < b.length; g++)
        Ab(a, b[g], c, d, f);
    else
      c = Bb(c), a && a[M] ? a.f.add(String(b), c, true, sa(d) ? !!d.capture : !!d, f) : Cb(a, b, c, true, d, f);
  }
  function Hb(a, b, c, d, f) {
    if (Array.isArray(b))
      for (var g = 0;g < b.length; g++)
        Hb(a, b[g], c, d, f);
    else
      (d = sa(d) ? !!d.capture : !!d, c = Bb(c), a && a[M]) ? a.f.remove(String(b), c, d, f) : a && (a = Db(a)) && (b = a.a[b.toString()], a = -1, b && (a = ub(b, c, d, f)), (c = -1 < a ? b[a] : null) && Ib(c));
  }
  function Ib(a) {
    if (typeof a !== "number" && a && !a.A) {
      var b = a.src;
      if (b && b[M])
        vb(b.f, a);
      else {
        var { type: c, proxy: d } = a;
        b.removeEventListener ? b.removeEventListener(c, d, a.capture) : b.detachEvent ? b.detachEvent(Fb(c), d) : b.addListener && b.removeListener && b.removeListener(d);
        yb--;
        (c = Db(b)) ? (vb(c, a), c.b == 0 && (c.src = null, b[wb] = null)) : sb(a);
      }
    }
  }
  function Fb(a) {
    return a in xb ? xb[a] : xb[a] = "on" + a;
  }
  function Gb(a, b) {
    if (a.A)
      a = true;
    else {
      b = new K(b, this);
      var c = a.listener, d = a.H || a.src;
      a.D && Ib(a);
      a = c.call(d, b);
    }
    return a;
  }
  function Db(a) {
    a = a[wb];
    return a instanceof tb ? a : null;
  }
  var Jb = "__closure_events_fn_" + (1e9 * Math.random() >>> 0);
  function Bb(a) {
    if (typeof a === "function")
      return a;
    a[Jb] || (a[Jb] = function(b) {
      return a.handleEvent(b);
    });
    return a[Jb];
  }
  function N() {
    lb.call(this);
    this.f = new tb(this);
    this.U = this;
  }
  B(N, lb);
  N.prototype[M] = true;
  N.prototype.addEventListener = function(a, b, c, d) {
    zb(this, a, b, c, d);
  };
  N.prototype.removeEventListener = function(a, b, c, d) {
    Hb(this, a, b, c, d);
  };
  function O(a, b) {
    a = a.U;
    var c = b.type || b;
    if (typeof b === "string")
      b = new J(b, a);
    else if (b instanceof J)
      b.target = b.target || a;
    else {
      var d = b;
      b = new J(c, a);
      Wa(b, d);
    }
    a = b.a = a;
    Kb(a, c, true, b);
    Kb(a, c, false, b);
  }
  function Kb(a, b, c, d) {
    if (b = a.f.a[String(b)]) {
      b = b.concat();
      for (var f = true, g = 0;g < b.length; ++g) {
        var e = b[g];
        if (e && !e.A && e.capture == c) {
          var h = e.listener, k = e.H || e.src;
          e.D && vb(a.f, e);
          f = h.call(k, d) !== false && f;
        }
      }
    }
  }
  var Lb = x;
  function Mb(a, b, c) {
    if (typeof a === "function")
      c && (a = z(a, c));
    else if (a && typeof a.handleEvent == "function")
      a = z(a.handleEvent, a);
    else
      throw Error("Invalid listener argument");
    return 2147483647 < Number(b) ? -1 : Lb.setTimeout(a, b || 0);
  }
  function Nb(a, b) {
    this.name = a;
    this.value = b;
  }
  Nb.prototype.toString = function() {
    return this.name;
  };
  var Ob = new Nb("OFF", Infinity);
  var Pb = new Nb("SEVERE", 1000);
  var Qb = new Nb("CONFIG", 700);
  var Rb = new Nb("FINE", 500);
  function Tb() {
    this.clear();
  }
  var Ub;
  Tb.prototype.clear = function() {};
  function Vb(a, b, c) {
    this.reset(a || Ob, b, c, undefined, undefined);
  }
  Vb.prototype.reset = function() {};
  function Wb(a, b) {
    this.a = null;
    this.f = [];
    this.b = (b === undefined ? null : b) || null;
    this.c = [];
    this.g = { getName: function() {
      return a;
    } };
  }
  function Xb(a) {
    if (a.a)
      return a.a;
    if (a.b)
      return Xb(a.b);
    Fa("Root logger has no level set.");
    return Ob;
  }
  function Yb(a, b) {
    for (;a; )
      a.f.forEach(function(c) {
        c(b);
      }), a = a.b;
  }
  function Zb() {
    this.entries = {};
    var a = new Wb("");
    a.a = Qb;
    this.entries[""] = a;
  }
  var $b;
  function ac(a, b, c) {
    var d = a.entries[b];
    if (d)
      return c !== undefined && (d.a = c), d;
    d = ac(a, b.substr(0, b.lastIndexOf(".")));
    var f = new Wb(b, d);
    a.entries[b] = f;
    d.c.push(f);
    c !== undefined && (f.a = c);
    return f;
  }
  function bc() {
    $b || ($b = new Zb);
    return $b;
  }
  function cc(a, b, c) {
    var d;
    if (d = a) {
      if (d = a && b) {
        d = b.value;
        var f = a ? Xb(ac(bc(), a.getName())) : Ob;
        d = d >= f.value;
      }
    }
    d && (b = b || Ob, d = ac(bc(), a.getName()), typeof c === "function" && (c = c()), Ub || (Ub = new Tb), a = a.getName(), a = new Vb(b, c, a), Yb(d, a));
  }
  function P(a, b) {
    a && cc(a, Rb, b);
  }
  function dc() {}
  dc.prototype.a = null;
  function ec(a) {
    var b;
    (b = a.a) || (b = {}, fc(a) && (b[0] = true, b[1] = true), b = a.a = b);
    return b;
  }
  var gc;
  function hc() {}
  B(hc, dc);
  function ic(a) {
    return (a = fc(a)) ? new ActiveXObject(a) : new XMLHttpRequest;
  }
  function fc(a) {
    if (!a.b && typeof XMLHttpRequest == "undefined" && typeof ActiveXObject != "undefined") {
      for (var b = ["MSXML2.XMLHTTP.6.0", "MSXML2.XMLHTTP.3.0", "MSXML2.XMLHTTP", "Microsoft.XMLHTTP"], c = 0;c < b.length; c++) {
        var d = b[c];
        try {
          return new ActiveXObject(d), a.b = d;
        } catch (f) {}
      }
      throw Error("Could not create ActiveXObject. ActiveX might be disabled, or MSXML might not be installed");
    }
    return a.b;
  }
  gc = new hc;
  t();
  u();
  function jc(a, b) {
    this.b = a[x.Symbol.iterator]();
    this.c = b;
    this.f = 0;
  }
  jc.prototype[Symbol.iterator] = function() {
    return this;
  };
  jc.prototype.next = function() {
    var a = this.b.next();
    return { value: a.done ? undefined : this.c.call(undefined, a.value, this.f++), done: a.done };
  };
  function kc(a, b) {
    return new jc(a, b);
  }
  t();
  u();
  t();
  u();
  var lc = "StopIteration" in x ? x.StopIteration : { message: "StopIteration", stack: "" };
  function Q() {}
  Q.prototype.next = function() {
    return Q.prototype.a.call(this);
  };
  Q.prototype.a = function() {
    throw lc;
  };
  Q.prototype.u = function() {
    return this;
  };
  function mc(a) {
    if (a instanceof R || a instanceof S || a instanceof T)
      return a;
    if (typeof a.next == "function")
      return new R(function() {
        return nc(a);
      });
    t();
    u();
    if (typeof a[Symbol.iterator] == "function")
      return t(), u(), new R(function() {
        return a[Symbol.iterator]();
      });
    if (typeof a.u == "function")
      return new R(function() {
        return nc(a.u());
      });
    throw Error("Not an iterator or iterable.");
  }
  function nc(a) {
    if (!(a instanceof Q))
      return a;
    var b = false;
    return { next: function() {
      for (var c;!b; )
        try {
          c = a.a();
          break;
        } catch (d) {
          if (d !== lc)
            throw d;
          b = true;
        }
      return { value: c, done: b };
    } };
  }
  t();
  u();
  function R(a) {
    this.b = a;
  }
  R.prototype.u = function() {
    return new S(this.b());
  };
  R.prototype[Symbol.iterator] = function() {
    return new T(this.b());
  };
  R.prototype.c = function() {
    return new T(this.b());
  };
  t();
  u();
  function S(a) {
    this.b = a;
  }
  oa(S, Q);
  S.prototype.a = function() {
    var a = this.b.next();
    if (a.done)
      throw lc;
    return a.value;
  };
  S.prototype.next = function() {
    return S.prototype.a.call(this);
  };
  S.prototype[Symbol.iterator] = function() {
    return new T(this.b);
  };
  S.prototype.c = function() {
    return new T(this.b);
  };
  function T(a) {
    R.call(this, function() {
      return a;
    });
    this.f = a;
  }
  oa(T, R);
  T.prototype.next = function() {
    return this.f.next();
  };
  function oc(a, b) {
    this.o = {};
    this.j = [];
    this.B = this.size = 0;
    var c = arguments.length;
    if (1 < c) {
      if (c % 2)
        throw Error("Uneven number of arguments");
      for (var d = 0;d < c; d += 2)
        this.set(arguments[d], arguments[d + 1]);
    } else
      a && this.addAll(a);
  }
  n = oc.prototype;
  n.G = function() {
    pc(this);
    return this.j.concat();
  };
  n.has = function(a) {
    return U(this.o, a);
  };
  n.clear = function() {
    this.o = {};
    this.B = this.size = this.j.length = 0;
  };
  n.remove = function(a) {
    return this.delete(a);
  };
  n.delete = function(a) {
    return U(this.o, a) ? (delete this.o[a], --this.size, this.B++, this.j.length > 2 * this.size && pc(this), true) : false;
  };
  function pc(a) {
    if (a.size != a.j.length) {
      for (var b = 0, c = 0;b < a.j.length; ) {
        var d = a.j[b];
        U(a.o, d) && (a.j[c++] = d);
        b++;
      }
      a.j.length = c;
    }
    if (a.size != a.j.length) {
      var f = {};
      for (c = b = 0;b < a.j.length; )
        d = a.j[b], U(f, d) || (a.j[c++] = d, f[d] = 1), b++;
      a.j.length = c;
    }
  }
  n.get = function(a, b) {
    return U(this.o, a) ? this.o[a] : b;
  };
  n.set = function(a, b) {
    U(this.o, a) || (this.size += 1, this.j.push(a), this.B++);
    this.o[a] = b;
  };
  n.addAll = function(a) {
    if (a instanceof oc)
      for (var b = a.G(), c = 0;c < b.length; c++)
        this.set(b[c], a.get(b[c]));
    else
      for (b in a)
        this.set(b, a[b]);
  };
  n.forEach = function(a, b) {
    for (var c = this.G(), d = 0;d < c.length; d++) {
      var f = c[d], g = this.get(f);
      a.call(b, g, f, this);
    }
  };
  n.clone = function() {
    return new oc(this);
  };
  n.keys = function() {
    return mc(this.u(true)).c();
  };
  n.values = function() {
    return mc(this.u(false)).c();
  };
  n.entries = function() {
    var a = this;
    return kc(this.keys(), function(b) {
      return [b, a.get(b)];
    });
  };
  n.u = function(a) {
    pc(this);
    var b = 0, c = this.B, d = this, f = new Q;
    f.a = function() {
      if (c != d.B)
        throw Error("The map has changed since the iterator was created");
      if (b >= d.j.length)
        throw lc;
      var g = d.j[b++];
      return a ? g : d.o[g];
    };
    f.next = f.a.bind(f);
    return f;
  };
  function U(a, b) {
    return Object.prototype.hasOwnProperty.call(a, b);
  }
  var qc = /^(?:([^:/?#.]+):)?(?:\/\/(?:([^\\/?#]*)@)?([^\\/?#]*?)(?::([0-9]+))?(?=[\\/?#]|$))?([^?#]+)?(?:\?([^#]*))?(?:#([\s\S]*))?$/;
  function rc(a) {
    N.call(this);
    this.headers = new oc;
    this.C = a || null;
    this.c = false;
    this.J = this.a = null;
    this.P = this.v = "";
    this.g = 0;
    this.l = "";
    this.i = this.N = this.s = this.L = false;
    this.h = 0;
    this.w = null;
    this.m = sc;
    this.I = this.M = false;
  }
  B(rc, N);
  var sc = "";
  rc.prototype.b = ac(bc(), "goog.net.XhrIo", undefined).g;
  var tc = /^https?$/i;
  var uc = ["POST", "PUT"];
  function vc(a, b, c) {
    if (a.a)
      throw Error("[goog.net.XhrIo] Object is active with another request=" + a.v + "; newUri=" + b);
    a.v = b;
    a.l = "";
    a.g = 0;
    a.P = "POST";
    a.L = false;
    a.c = true;
    a.a = a.C ? ic(a.C) : ic(gc);
    a.J = a.C ? ec(a.C) : ec(gc);
    a.a.onreadystatechange = z(a.R, a);
    try {
      P(a.b, V(a, "Opening Xhr")), a.N = true, a.a.open("POST", String(b), true), a.N = false;
    } catch (g) {
      P(a.b, V(a, "Error opening Xhr: " + g.message));
      wc(a, g);
      return;
    }
    b = c || "";
    c = a.headers.clone();
    var d = c.G().find(function(g) {
      return g.toLowerCase() == "content-type";
    }), f = x.FormData && b instanceof x.FormData;
    !(0 <= Oa(uc, "POST")) || d || f || c.set("Content-Type", "application/x-www-form-urlencoded;charset=utf-8");
    c.forEach(function(g, e) {
      this.a.setRequestHeader(e, g);
    }, a);
    a.m && (a.a.responseType = a.m);
    "withCredentials" in a.a && a.a.withCredentials !== a.M && (a.a.withCredentials = a.M);
    try {
      xc(a), 0 < a.h && (a.I = yc(a.a), P(a.b, V(a, "Will abort after " + a.h + "ms if incomplete, xhr2 " + a.I)), a.I ? (a.a.timeout = a.h, a.a.ontimeout = z(a.T, a)) : a.w = Mb(a.T, a.h, a)), P(a.b, V(a, "Sending request")), a.s = true, a.a.send(b), a.s = false;
    } catch (g) {
      P(a.b, V(a, "Send error: " + g.message)), wc(a, g);
    }
  }
  function yc(a) {
    return bb && kb() && typeof a.timeout === "number" && a.ontimeout !== undefined;
  }
  n = rc.prototype;
  n.T = function() {
    typeof qa != "undefined" && this.a && (this.l = "Timed out after " + this.h + "ms, aborting", this.g = 8, P(this.b, V(this, this.l)), O(this, "timeout"), this.abort(8));
  };
  function wc(a, b) {
    a.c = false;
    a.a && (a.i = true, a.a.abort(), a.i = false);
    a.l = b;
    a.g = 5;
    zc(a);
    Ac(a);
  }
  function zc(a) {
    a.L || (a.L = true, O(a, "complete"), O(a, "error"));
  }
  n.abort = function(a) {
    this.a && this.c && (P(this.b, V(this, "Aborting")), this.c = false, this.i = true, this.a.abort(), this.i = false, this.g = a || 7, O(this, "complete"), O(this, "abort"), Ac(this));
  };
  n.R = function() {
    this.K || (this.N || this.s || this.i ? Bc(this) : this.W());
  };
  n.W = function() {
    Bc(this);
  };
  function Bc(a) {
    if (a.c && typeof qa != "undefined") {
      if (a.J[1] && W(a) == 4 && a.getStatus() == 2)
        P(a.b, V(a, "Local request error detected and ignored"));
      else if (a.s && W(a) == 4)
        Mb(a.R, 0, a);
      else if (O(a, "readystatechange"), W(a) == 4) {
        P(a.b, V(a, "Request complete"));
        a.c = false;
        try {
          var b = a.getStatus();
          a:
            switch (b) {
              case 200:
              case 201:
              case 202:
              case 204:
              case 206:
              case 304:
              case 1223:
                var c = true;
                break a;
              default:
                c = false;
            }
          var d;
          if (!(d = c)) {
            var f;
            if (f = b === 0) {
              var g = String(a.v).match(qc)[1] || null;
              if (!g && x.self && x.self.location) {
                var e = x.self.location.protocol;
                g = e.substr(0, e.length - 1);
              }
              f = !tc.test(g ? g.toLowerCase() : "");
            }
            d = f;
          }
          if (d)
            O(a, "complete"), O(a, "success");
          else {
            a.g = 6;
            try {
              var h = 2 < W(a) ? a.a.statusText : "";
            } catch (k) {
              P(a.b, "Can not get status: " + k.message), h = "";
            }
            a.l = h + " [" + a.getStatus() + "]";
            zc(a);
          }
        } finally {
          Ac(a);
        }
      }
    }
  }
  function Ac(a) {
    if (a.a) {
      xc(a);
      var b = a.a, c = a.J[0] ? ra : null;
      a.a = null;
      a.J = null;
      O(a, "ready");
      try {
        b.onreadystatechange = c;
      } catch (d) {
        (a = a.b) && cc(a, Pb, "Problem encountered resetting onreadystatechange: " + d.message);
      }
    }
  }
  function xc(a) {
    a.a && a.I && (a.a.ontimeout = null);
    a.w && (Lb.clearTimeout(a.w), a.w = null);
  }
  function W(a) {
    return a.a ? a.a.readyState : 0;
  }
  n.getStatus = function() {
    try {
      return 2 < W(this) ? this.a.status : -1;
    } catch (a) {
      return -1;
    }
  };
  function Cc(a) {
    try {
      if (!a.a)
        return null;
      if ("response" in a.a)
        return a.a.response;
      switch (a.m) {
        case sc:
        case "text":
          return a.a.responseText;
        case "arraybuffer":
          if ("mozResponseArrayBuffer" in a.a)
            return a.a.mozResponseArrayBuffer;
      }
      var b = a.b;
      b && cc(b, Pb, "Response type " + a.m + " is not supported on this browser");
      return null;
    } catch (c) {
      return P(a.b, "Can not get response: " + c.message), null;
    }
  }
  function Dc(a) {
    var b = {};
    a = (a.a && W(a) == 4 ? a.a.getAllResponseHeaders() || "" : "").split(`\r
`);
    for (var c = 0;c < a.length; c++)
      if (!/^[\s\xa0]*$/.test(a[c])) {
        var d = Xa(a[c]), f = d[0];
        d = d[1];
        if (typeof d === "string") {
          d = d.trim();
          var g = b[f] || [];
          b[f] = g;
          g.push(d);
        }
      }
    return Ua(b, function(e) {
      return e.join(", ");
    });
  }
  function V(a, b) {
    return b + " [" + a.P + " " + a.v + " " + a.getStatus() + "]";
  }
  var Ec = {};
  var Fc = null;
  function Gc(a) {
    var b = a.length, c = 3 * b / 4;
    c % 3 ? c = Math.floor(c) : H("=.", a[b - 1]) && (c = H("=.", a[b - 2]) ? c - 2 : c - 1);
    var d = new Uint8Array(c), f = 0;
    Hc(a, function(g) {
      d[f++] = g;
    });
    return d.subarray(0, f);
  }
  function Hc(a, b) {
    function c(k) {
      for (;d < a.length; ) {
        var l = a.charAt(d++), m = Fc[l];
        if (m != null)
          return m;
        if (!/^[\s\xa0]*$/.test(l))
          throw Error("Unknown base64 encoding at char: " + l);
      }
      return k;
    }
    Ic();
    for (var d = 0;; ) {
      var f = c(-1), g = c(0), e = c(64), h = c(64);
      if (h === 64 && f === -1)
        break;
      b(f << 2 | g >> 4);
      e != 64 && (b(g << 4 & 240 | e >> 2), h != 64 && b(e << 6 & 192 | h));
    }
  }
  function Ic() {
    if (!Fc) {
      Fc = {};
      for (var a = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".split(""), b = ["+/=", "+/", "-_=", "-_.", "-_"], c = 0;5 > c; c++) {
        var d = a.concat(b[c].split(""));
        Ec[c] = d;
        for (var f = 0;f < d.length; f++) {
          var g = d[f];
          Fc[g] === undefined && (Fc[g] = f);
        }
      }
    }
  }
  var Jc = ["content-type", "grpc-status", "grpc-message"];
  function X(a) {
    this.a = a.Z;
    this.m = null;
    this.b = [];
    this.h = [];
    this.g = [];
    this.f = [];
    this.c = [];
    this.l = false;
    this.i = 0;
    this.s = new Ga;
    var b = this;
    zb(this.a, "readystatechange", function() {
      var c = b.a;
      if (c = c.a ? c.a.getResponseHeader("Content-Type") : null) {
        c = c.toLowerCase();
        if (c.lastIndexOf("application/grpc-web-text", 0) == 0) {
          c = b.a;
          try {
            var d = c.a ? c.a.responseText : "";
          } catch (k) {
            P(c.b, "Can not get responseText: " + k.message), d = "";
          }
          c = d || "";
          d = c.length - c.length % 4;
          c = c.substr(b.i, d - b.i);
          if (c.length == 0)
            return;
          b.i = d;
          c = Gc(c);
        } else if (c.lastIndexOf("application/grpc", 0) == 0)
          c = new Uint8Array(Cc(b.a));
        else {
          Y(b, new E(2, "Unknown Content-type received."));
          return;
        }
        d = null;
        try {
          d = Ia(b.s, c);
        } catch (k) {
          Y(b, new E(2, "Error in parsing response body"));
        }
        if (d)
          for (c = 0;c < d.length; c++) {
            if (Ja in d[c]) {
              var f = d[c][Ja];
              if (f) {
                var g = false, e = undefined;
                try {
                  e = b.m(f), g = true;
                } catch (k) {
                  Y(b, new E(13, "Error when deserializing response data; error: " + k + (", response: " + e)));
                }
                if (g)
                  for (f = e, g = 0;g < b.b.length; g++)
                    b.b[g](f);
              }
            }
            if (G in d[c] && 0 < d[c][G].length) {
              f = "";
              for (g = 0;g < d[c][G].length; g++)
                f += String.fromCharCode(d[c][G][g]);
              f = f.trim().split(`\r
`);
              g = {};
              for (e = 0;e < f.length; e++) {
                var h = f[e].indexOf(":");
                g[f[e].substring(0, h).trim()] = f[e].substring(h + 1).trim();
              }
              f = g;
              g = 0;
              e = "";
              "grpc-status" in f && (g = Number(f["grpc-status"]), delete f["grpc-status"]);
              "grpc-message" in f && (e = f["grpc-message"], delete f["grpc-message"]);
              Y(b, new E(g, e, f));
            }
          }
      }
    });
    zb(this.a, "complete", function() {
      var c = b.a.g, d = 2, f = "", g = {};
      d = Dc(b.a);
      var e = {};
      for (h in d)
        d.hasOwnProperty(h) && (e[h.toLowerCase()] = d[h]);
      Object.keys(e).forEach(function(k) {
        Jc.includes(k) || (g[k] = e[k]);
      });
      Kc(b, g);
      var h = -1;
      if (c != 0) {
        switch (c) {
          case 7:
            d = 10;
            break;
          case 8:
            d = 4;
            break;
          case 6:
            h = b.a.getStatus();
            d = Aa(h);
            break;
          default:
            d = 14;
        }
        d == 10 && b.l || (f = Da(c), h != -1 && (f += ", http status code: " + h), Y(b, new E(d, f)));
      } else
        c = false, "grpc-status" in e && (d = Number(e["grpc-status"]), ("grpc-message" in e) && (f = e["grpc-message"]), d != 0 && (Y(b, new E(d, f || "", e)), c = true)), c || Lc(b);
    });
  }
  X.prototype.on = function(a, b) {
    a == "data" ? this.b.push(b) : a == "status" ? this.h.push(b) : a == "metadata" ? this.g.push(b) : a == "end" ? this.c.push(b) : a == "error" && this.f.push(b);
    return this;
  };
  function Mc(a, b) {
    b = a.indexOf(b);
    -1 < b && a.splice(b, 1);
  }
  X.prototype.removeListener = function(a, b) {
    a == "data" ? Mc(this.b, b) : a == "status" ? Mc(this.h, b) : a == "metadata" ? Mc(this.g, b) : a == "end" ? Mc(this.c, b) : a == "error" && Mc(this.f, b);
    return this;
  };
  X.prototype.cancel = function() {
    this.l = true;
    this.a.abort();
  };
  function Y(a, b) {
    if (b.code != 0)
      for (var c = new E(b.code, decodeURIComponent(b.message || ""), b.metadata), d = 0;d < a.f.length; d++)
        a.f[d](c);
    b = { code: b.code, details: decodeURIComponent(b.message || ""), metadata: b.metadata };
    for (c = 0;c < a.h.length; c++)
      a.h[c](b);
  }
  function Kc(a, b) {
    for (var c = 0;c < a.g.length; c++)
      a.g[c](b);
  }
  function Lc(a) {
    for (var b = 0;b < a.c.length; b++)
      a.c[b]();
  }
  X.prototype.cancel = X.prototype.cancel;
  X.prototype.removeListener = X.prototype.removeListener;
  X.prototype.on = X.prototype.on;
  function Nc(a) {
    var b = "";
    Ta(a, function(c, d) {
      b += d;
      b += ":";
      b += c;
      b += `\r
`;
    });
    return b;
  }
  function Z(a, b) {
    a = a === undefined ? {} : a;
    this.a = a.format || y("format", a) || "text";
    this.g = a.aa || y("suppressCorsPreflight", a) || false;
    this.f = a.withCredentials || y("withCredentials", a) || false;
    this.b = a.$ || y("streamInterceptors", a) || [];
    this.h = a.ba || y("unaryInterceptors", a) || [];
    this.c = b || null;
  }
  Z.prototype.X = function(a, b, c, d, f) {
    var g = this, e = a.substr(0, a.length - d.name.length);
    a = Oc(function(h) {
      return Pc(g, h, e);
    }, this.b).call(this, za(d, b, c));
    Qc(a, f, false);
    return new Ca(a);
  };
  Z.prototype.S = function(a, b, c, d) {
    var f = this, g = a.substr(0, a.length - d.name.length);
    return Oc(function(e) {
      return new Promise(function(h, k) {
        var l = Pc(f, e, g), m, p, q;
        Qc(l, function(w, A, L, Sb, Rc) {
          w ? k(w) : Rc ? q = A : L ? p = L : Sb ? m = Sb : (w = e.getMethodDescriptor(), A = m, A = A === undefined ? {} : A, h(new D(q, w, A, p === undefined ? null : p)));
        }, true);
      });
    }, this.h).call(this, za(d, b, c)).then(function(e) {
      return e.getResponseMessage();
    });
  };
  Z.prototype.unaryCall = function(a, b, c, d) {
    return this.S(a, b, c, d);
  };
  Z.prototype.Y = function(a, b, c, d) {
    var f = this, g = a.substr(0, a.length - d.name.length);
    return Oc(function(e) {
      return Pc(f, e, g);
    }, this.b).call(this, za(d, b, c));
  };
  function Pc(a, b, c) {
    var d = b.getMethodDescriptor(), f = c + d.getName();
    c = a.c ? a.c : new rc;
    c.M = a.f;
    var g = new X({ Z: c });
    g.m = d.b;
    var e = b.getMetadata();
    for (h in e)
      c.headers.set(h, e[h]);
    a.a == "text" ? (c.headers.set("Content-Type", "application/grpc-web-text"), c.headers.set("Accept", "application/grpc-web-text")) : c.headers.set("Content-Type", "application/grpc-web+proto");
    c.headers.set("X-User-Agent", "grpc-web-javascript/0.1");
    c.headers.set("X-Grpc-Web", "1");
    if (c.headers.has("deadline")) {
      var h = Number(c.headers.get("deadline"));
      h = Math.ceil(h - new Date().getTime());
      c.headers.delete("deadline");
      h === Infinity && (h = 0);
      0 < h && (c.headers.set("grpc-timeout", h + "m"), c.h = Math.max(0, Math.max(1000, Math.ceil(1.1 * h))));
    }
    if (a.g) {
      e = c.headers;
      h = {};
      for (var k = ha(e.keys()), l = k.next();!l.done; l = k.next())
        l = l.value, h[l] = e.get(l);
      c.headers.clear();
      b: {
        for (m in h) {
          var m = false;
          break b;
        }
        m = true;
      }
      if (!m)
        if (h = Nc(h), typeof f === "string") {
          if (m = encodeURIComponent("$httpHeaders"), h = h != null ? "=" + encodeURIComponent(String(h)) : "", m += h)
            h = f.indexOf("#"), 0 > h && (h = f.length), e = f.indexOf("?"), 0 > e || e > h ? (e = h, k = "") : k = f.substring(e + 1, h), f = [f.substr(0, e), k, f.substr(h)], h = f[1], f[1] = m ? h ? h + "&" + m : m : h, f = f[0] + (f[1] ? "?" + f[1] : "") + f[2];
        } else
          f.a("$httpHeaders", h);
    }
    b = (0, d.a)(b.getRequestMessage());
    d = b.length;
    m = [0, 0, 0, 0];
    h = new Uint8Array(5 + d);
    for (e = 3;0 <= e; e--)
      m[e] = d % 256, d >>>= 8;
    h.set(new Uint8Array(m), 1);
    h.set(b, 5);
    b = h;
    if (a.a == "text") {
      a = b;
      var p;
      p === undefined && (p = 0);
      Ic();
      p = Ec[p];
      b = Array(Math.floor(a.length / 3));
      d = p[64] || "";
      for (m = h = 0;h < a.length - 2; h += 3) {
        l = a[h];
        var q = a[h + 1];
        k = a[h + 2];
        e = p[l >> 2];
        l = p[(l & 3) << 4 | q >> 4];
        q = p[(q & 15) << 2 | k >> 6];
        k = p[k & 63];
        b[m++] = e + l + q + k;
      }
      e = 0;
      k = d;
      switch (a.length - h) {
        case 2:
          e = a[h + 1], k = p[(e & 15) << 2] || d;
        case 1:
          a = a[h], b[m] = p[a >> 2] + p[(a & 3) << 4 | e >> 4] + k + d;
      }
      b = b.join("");
    } else
      a.a == "binary" && (c.m = "arraybuffer");
    vc(c, f, b);
    return g;
  }
  function Qc(a, b, c) {
    var d = false, f = null, g = false;
    a.on("data", function(e) {
      d = true;
      f = e;
    });
    a.on("error", function(e) {
      e.code == 0 || g || (g = true, b(e, null));
    });
    a.on("status", function(e) {
      e.code == 0 || g ? c && b(null, null, e) : (g = true, b({ code: e.code, message: e.details, metadata: e.metadata }, null));
    });
    if (c)
      a.on("metadata", function(e) {
        b(null, null, null, e);
      });
    a.on("end", function() {
      g || (d ? c ? b(null, f, null, null, true) : b(null, f) : b({ code: 2, message: "Incomplete response" }));
      c && b(null, null);
    });
  }
  function Oc(a, b) {
    var c = a;
    b.forEach(function(d) {
      var f = c;
      c = function(g) {
        return d.intercept(g, f);
      };
    });
    return c;
  }
  Z.prototype.serverStreaming = Z.prototype.Y;
  Z.prototype.unaryCall = Z.prototype.unaryCall;
  Z.prototype.thenableCall = Z.prototype.S;
  Z.prototype.rpcCall = Z.prototype.X;
  module.exports.CallOptions = xa;
  module.exports.MethodDescriptor = ya;
  module.exports.GrpcWebClientBase = Z;
  module.exports.RpcError = E;
  module.exports.StatusCode = { OK: 0, CANCELLED: 1, UNKNOWN: 2, INVALID_ARGUMENT: 3, DEADLINE_EXCEEDED: 4, NOT_FOUND: 5, ALREADY_EXISTS: 6, PERMISSION_DENIED: 7, UNAUTHENTICATED: 16, RESOURCE_EXHAUSTED: 8, FAILED_PRECONDITION: 9, ABORTED: 10, OUT_OF_RANGE: 11, UNIMPLEMENTED: 12, INTERNAL: 13, UNAVAILABLE: 14, DATA_LOSS: 15 };
  module.exports.MethodType = { UNARY: "unary", SERVER_STREAMING: "server_streaming", BIDI_STREAMING: "bidi_streaming" };
  Lb = typeof globalThis !== "undefined" && globalThis || self;
});

// node_modules/google-protobuf/google-protobuf.js
var require_google_protobuf = __commonJS(function(exports) {
  var aa = typeof Object.defineProperties == "function" ? Object.defineProperty : function(a, b, c) {
    a != Array.prototype && a != Object.prototype && (a[b] = c.value);
  };
  var e = typeof window != "undefined" && window === exports ? exports : typeof global != "undefined" && global != null ? global : exports;
  function ba(a, b) {
    if (b) {
      var c = e;
      a = a.split(".");
      for (var d = 0;d < a.length - 1; d++) {
        var f = a[d];
        f in c || (c[f] = {});
        c = c[f];
      }
      a = a[a.length - 1];
      d = c[a];
      b = b(d);
      b != d && b != null && aa(c, a, { configurable: true, writable: true, value: b });
    }
  }
  function ca(a) {
    var b = 0;
    return function() {
      return b < a.length ? { done: false, value: a[b++] } : { done: true };
    };
  }
  function da() {
    da = function() {};
    e.Symbol || (e.Symbol = ea);
  }
  function fa(a, b) {
    this.a = a;
    aa(this, "description", { configurable: true, writable: true, value: b });
  }
  fa.prototype.toString = function() {
    return this.a;
  };
  var ea = function() {
    function a(c) {
      if (this instanceof a)
        throw new TypeError("Symbol is not a constructor");
      return new fa("jscomp_symbol_" + (c || "") + "_" + b++, c);
    }
    var b = 0;
    return a;
  }();
  function ha() {
    da();
    var a = e.Symbol.iterator;
    a || (a = e.Symbol.iterator = e.Symbol("Symbol.iterator"));
    typeof Array.prototype[a] != "function" && aa(Array.prototype, a, { configurable: true, writable: true, value: function() {
      return ia(ca(this));
    } });
    ha = function() {};
  }
  function ia(a) {
    ha();
    a = { next: a };
    a[e.Symbol.iterator] = function() {
      return this;
    };
    return a;
  }
  function ja(a, b) {
    ha();
    a instanceof String && (a += "");
    var c = 0, d = { next: function() {
      if (c < a.length) {
        var f = c++;
        return { value: b(f, a[f]), done: false };
      }
      d.next = function() {
        return { done: true, value: undefined };
      };
      return d.next();
    } };
    d[Symbol.iterator] = function() {
      return d;
    };
    return d;
  }
  ba("Array.prototype.entries", function(a) {
    return a ? a : function() {
      return ja(this, function(b, c) {
        return [b, c];
      });
    };
  });
  var ka = exports || self;
  function g(a, b, c) {
    a = a.split(".");
    c = c || ka;
    a[0] in c || typeof c.execScript == "undefined" || c.execScript("var " + a[0]);
    for (var d;a.length && (d = a.shift()); )
      a.length || b === undefined ? c[d] && c[d] !== Object.prototype[d] ? c = c[d] : c = c[d] = {} : c[d] = b;
  }
  function k(a) {
    var b = typeof a;
    if (b == "object")
      if (a) {
        if (a instanceof Array)
          return "array";
        if (a instanceof Object)
          return b;
        var c = Object.prototype.toString.call(a);
        if (c == "[object Window]")
          return "object";
        if (c == "[object Array]" || typeof a.length == "number" && typeof a.splice != "undefined" && typeof a.propertyIsEnumerable != "undefined" && !a.propertyIsEnumerable("splice"))
          return "array";
        if (c == "[object Function]" || typeof a.call != "undefined" && typeof a.propertyIsEnumerable != "undefined" && !a.propertyIsEnumerable("call"))
          return "function";
      } else
        return "null";
    else if (b == "function" && typeof a.call == "undefined")
      return "object";
    return b;
  }
  function la(a) {
    var b = typeof a;
    return b == "object" && a != null || b == "function";
  }
  function ma(a, b, c) {
    g(a, b, c);
  }
  function na(a, b) {
    function c() {}
    c.prototype = b.prototype;
    a.prototype = new c;
    a.prototype.constructor = a;
  }
  var oa = "constructor hasOwnProperty isPrototypeOf propertyIsEnumerable toLocaleString toString valueOf".split(" ");
  function pa(a, b) {
    for (var c, d, f = 1;f < arguments.length; f++) {
      d = arguments[f];
      for (c in d)
        a[c] = d[c];
      for (var h = 0;h < oa.length; h++)
        c = oa[h], Object.prototype.hasOwnProperty.call(d, c) && (a[c] = d[c]);
    }
  }
  var qa = Array.prototype.forEach ? function(a, b) {
    Array.prototype.forEach.call(a, b, undefined);
  } : function(a, b) {
    for (var c = a.length, d = typeof a === "string" ? a.split("") : a, f = 0;f < c; f++)
      f in d && b.call(undefined, d[f], f, a);
  };
  var l = Array.prototype.map ? function(a, b) {
    return Array.prototype.map.call(a, b, undefined);
  } : function(a, b) {
    for (var c = a.length, d = Array(c), f = typeof a === "string" ? a.split("") : a, h = 0;h < c; h++)
      h in f && (d[h] = b.call(undefined, f[h], h, a));
    return d;
  };
  function ra(a, b, c) {
    return 2 >= arguments.length ? Array.prototype.slice.call(a, b) : Array.prototype.slice.call(a, b, c);
  }
  function sa(a, b, c, d) {
    var f = "Assertion failed";
    if (c) {
      f += ": " + c;
      var h = d;
    } else
      a && (f += ": " + a, h = b);
    throw Error(f, h || []);
  }
  function n(a, b, c) {
    for (var d = [], f = 2;f < arguments.length; ++f)
      d[f - 2] = arguments[f];
    a || sa("", null, b, d);
    return a;
  }
  function ta(a, b, c) {
    for (var d = [], f = 2;f < arguments.length; ++f)
      d[f - 2] = arguments[f];
    typeof a !== "string" && sa("Expected string but got %s: %s.", [k(a), a], b, d);
  }
  function ua(a, b, c) {
    for (var d = [], f = 2;f < arguments.length; ++f)
      d[f - 2] = arguments[f];
    Array.isArray(a) || sa("Expected array but got %s: %s.", [k(a), a], b, d);
  }
  function p(a, b) {
    for (var c = [], d = 1;d < arguments.length; ++d)
      c[d - 1] = arguments[d];
    throw Error("Failure" + (a ? ": " + a : ""), c);
  }
  function q(a, b, c, d) {
    for (var f = [], h = 3;h < arguments.length; ++h)
      f[h - 3] = arguments[h];
    a instanceof b || sa("Expected instanceof %s but got %s.", [va(b), va(a)], c, f);
  }
  function va(a) {
    return a instanceof Function ? a.displayName || a.name || "unknown type name" : a instanceof Object ? a.constructor.displayName || a.constructor.name || Object.prototype.toString.call(a) : a === null ? "null" : typeof a;
  }
  function r(a, b) {
    this.c = a;
    this.b = b;
    this.a = {};
    this.arrClean = true;
    if (0 < this.c.length) {
      for (a = 0;a < this.c.length; a++) {
        b = this.c[a];
        var c = b[0];
        this.a[c.toString()] = new wa(c, b[1]);
      }
      this.arrClean = true;
    }
  }
  g("jspb.Map", r, undefined);
  r.prototype.g = function() {
    if (this.arrClean) {
      if (this.b) {
        var a = this.a, b;
        for (b in a)
          if (Object.prototype.hasOwnProperty.call(a, b)) {
            var c = a[b].a;
            c && c.g();
          }
      }
    } else {
      this.c.length = 0;
      a = u(this);
      a.sort();
      for (b = 0;b < a.length; b++) {
        var d = this.a[a[b]];
        (c = d.a) && c.g();
        this.c.push([d.key, d.value]);
      }
      this.arrClean = true;
    }
    return this.c;
  };
  r.prototype.toArray = r.prototype.g;
  r.prototype.Mc = function(a, b) {
    for (var c = this.g(), d = [], f = 0;f < c.length; f++) {
      var h = this.a[c[f][0].toString()];
      v(this, h);
      var m = h.a;
      m ? (n(b), d.push([h.key, b(a, m)])) : d.push([h.key, h.value]);
    }
    return d;
  };
  r.prototype.toObject = r.prototype.Mc;
  r.fromObject = function(a, b, c) {
    b = new r([], b);
    for (var d = 0;d < a.length; d++) {
      var f = a[d][0], h = c(a[d][1]);
      b.set(f, h);
    }
    return b;
  };
  function w(a) {
    this.a = 0;
    this.b = a;
  }
  w.prototype.next = function() {
    return this.a < this.b.length ? { done: false, value: this.b[this.a++] } : { done: true, value: undefined };
  };
  typeof Symbol != "undefined" && (w.prototype[Symbol.iterator] = function() {
    return this;
  });
  r.prototype.Jb = function() {
    return u(this).length;
  };
  r.prototype.getLength = r.prototype.Jb;
  r.prototype.clear = function() {
    this.a = {};
    this.arrClean = false;
  };
  r.prototype.clear = r.prototype.clear;
  r.prototype.Cb = function(a) {
    a = a.toString();
    var b = this.a.hasOwnProperty(a);
    delete this.a[a];
    this.arrClean = false;
    return b;
  };
  r.prototype.del = r.prototype.Cb;
  r.prototype.Eb = function() {
    var a = [], b = u(this);
    b.sort();
    for (var c = 0;c < b.length; c++) {
      var d = this.a[b[c]];
      a.push([d.key, d.value]);
    }
    return a;
  };
  r.prototype.getEntryList = r.prototype.Eb;
  r.prototype.entries = function() {
    var a = [], b = u(this);
    b.sort();
    for (var c = 0;c < b.length; c++) {
      var d = this.a[b[c]];
      a.push([d.key, v(this, d)]);
    }
    return new w(a);
  };
  r.prototype.entries = r.prototype.entries;
  r.prototype.keys = function() {
    var a = [], b = u(this);
    b.sort();
    for (var c = 0;c < b.length; c++)
      a.push(this.a[b[c]].key);
    return new w(a);
  };
  r.prototype.keys = r.prototype.keys;
  r.prototype.values = function() {
    var a = [], b = u(this);
    b.sort();
    for (var c = 0;c < b.length; c++)
      a.push(v(this, this.a[b[c]]));
    return new w(a);
  };
  r.prototype.values = r.prototype.values;
  r.prototype.forEach = function(a, b) {
    var c = u(this);
    c.sort();
    for (var d = 0;d < c.length; d++) {
      var f = this.a[c[d]];
      a.call(b, v(this, f), f.key, this);
    }
  };
  r.prototype.forEach = r.prototype.forEach;
  r.prototype.set = function(a, b) {
    var c = new wa(a);
    this.b ? (c.a = b, c.value = b.g()) : c.value = b;
    this.a[a.toString()] = c;
    this.arrClean = false;
    return this;
  };
  r.prototype.set = r.prototype.set;
  function v(a, b) {
    return a.b ? (b.a || (b.a = new a.b(b.value)), b.a) : b.value;
  }
  r.prototype.get = function(a) {
    if (a = this.a[a.toString()])
      return v(this, a);
  };
  r.prototype.get = r.prototype.get;
  r.prototype.has = function(a) {
    return a.toString() in this.a;
  };
  r.prototype.has = r.prototype.has;
  r.prototype.Jc = function(a, b, c, d, f) {
    var h = u(this);
    h.sort();
    for (var m = 0;m < h.length; m++) {
      var t = this.a[h[m]];
      b.Va(a);
      c.call(b, 1, t.key);
      this.b ? d.call(b, 2, v(this, t), f) : d.call(b, 2, t.value);
      b.Ya();
    }
  };
  r.prototype.serializeBinary = r.prototype.Jc;
  r.deserializeBinary = function(a, b, c, d, f, h, m) {
    for (;b.oa() && !b.bb(); ) {
      var t = b.c;
      t == 1 ? h = c.call(b) : t == 2 && (a.b ? (n(f), m || (m = new a.b), d.call(b, m, f)) : m = d.call(b));
    }
    n(h != null);
    n(m != null);
    a.set(h, m);
  };
  function u(a) {
    a = a.a;
    var b = [], c;
    for (c in a)
      Object.prototype.hasOwnProperty.call(a, c) && b.push(c);
    return b;
  }
  function wa(a, b) {
    this.key = a;
    this.value = b;
    this.a = undefined;
  }
  function xa(a) {
    if (8192 >= a.length)
      return String.fromCharCode.apply(null, a);
    for (var b = "", c = 0;c < a.length; c += 8192)
      b += String.fromCharCode.apply(null, ra(a, c, c + 8192));
    return b;
  }
  var ya = { "\x00": "\\0", "\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t", "\v": "\\x0B", '"': "\\\"", "\\": "\\\\", "<": "\\u003C" };
  var za = { "'": "\\'" };
  var Aa = {};
  var x = null;
  function Ba(a, b) {
    b === undefined && (b = 0);
    Ca();
    b = Aa[b];
    for (var c = [], d = 0;d < a.length; d += 3) {
      var f = a[d], h = d + 1 < a.length, m = h ? a[d + 1] : 0, t = d + 2 < a.length, B = t ? a[d + 2] : 0, M = f >> 2;
      f = (f & 3) << 4 | m >> 4;
      m = (m & 15) << 2 | B >> 6;
      B &= 63;
      t || (B = 64, h || (m = 64));
      c.push(b[M], b[f], b[m] || "", b[B] || "");
    }
    return c.join("");
  }
  function Da(a) {
    var b = a.length, c = 3 * b / 4;
    c % 3 ? c = Math.floor(c) : "=.".indexOf(a[b - 1]) != -1 && (c = "=.".indexOf(a[b - 2]) != -1 ? c - 2 : c - 1);
    var d = new Uint8Array(c), f = 0;
    Ea(a, function(h) {
      d[f++] = h;
    });
    return d.subarray(0, f);
  }
  function Ea(a, b) {
    function c(B) {
      for (;d < a.length; ) {
        var M = a.charAt(d++), La = x[M];
        if (La != null)
          return La;
        if (!/^[\s\xa0]*$/.test(M))
          throw Error("Unknown base64 encoding at char: " + M);
      }
      return B;
    }
    Ca();
    for (var d = 0;; ) {
      var f = c(-1), h = c(0), m = c(64), t = c(64);
      if (t === 64 && f === -1)
        break;
      b(f << 2 | h >> 4);
      m != 64 && (b(h << 4 & 240 | m >> 2), t != 64 && b(m << 6 & 192 | t));
    }
  }
  function Ca() {
    if (!x) {
      x = {};
      for (var a = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".split(""), b = ["+/=", "+/", "-_=", "-_.", "-_"], c = 0;5 > c; c++) {
        var d = a.concat(b[c].split(""));
        Aa[c] = d;
        for (var f = 0;f < d.length; f++) {
          var h = d[f];
          x[h] === undefined && (x[h] = f);
        }
      }
    }
  }
  g("jspb.ConstBinaryMessage", function() {}, undefined);
  g("jspb.BinaryMessage", function() {}, undefined);
  g("jspb.BinaryConstants.FieldType", { yb: -1, ee: 1, FLOAT: 2, ke: 3, te: 4, je: 5, xb: 6, wb: 7, BOOL: 8, re: 9, ie: 10, le: 11, ce: 12, se: 13, ge: 14, me: 15, ne: 16, oe: 17, pe: 18, he: 30, ve: 31 }, undefined);
  g("jspb.BinaryConstants.WireType", { yb: -1, ue: 0, xb: 1, de: 2, qe: 3, fe: 4, wb: 5 }, undefined);
  g("jspb.BinaryConstants.FieldTypeToWireType", function(a) {
    switch (a) {
      case 5:
      case 3:
      case 13:
      case 4:
      case 17:
      case 18:
      case 8:
      case 14:
      case 31:
        return 0;
      case 1:
      case 6:
      case 16:
      case 30:
        return 1;
      case 9:
      case 11:
      case 12:
        return 2;
      case 2:
      case 7:
      case 15:
        return 5;
      default:
        return -1;
    }
  }, undefined);
  g("jspb.BinaryConstants.INVALID_FIELD_NUMBER", -1, undefined);
  g("jspb.BinaryConstants.FLOAT32_EPS", 0.000000000000000000000000000000000000000000001401298464324817, undefined);
  g("jspb.BinaryConstants.FLOAT32_MIN", 0.000000000000000000000000000000000000011754943508222875, undefined);
  g("jspb.BinaryConstants.FLOAT32_MAX", 340282346638528860000000000000000000000, undefined);
  g("jspb.BinaryConstants.FLOAT64_EPS", 0.000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000005, undefined);
  g("jspb.BinaryConstants.FLOAT64_MIN", 0.000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000022250738585072014, undefined);
  g("jspb.BinaryConstants.FLOAT64_MAX", 179769313486231570000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000, undefined);
  g("jspb.BinaryConstants.TWO_TO_20", 1048576, undefined);
  g("jspb.BinaryConstants.TWO_TO_23", 8388608, undefined);
  g("jspb.BinaryConstants.TWO_TO_31", 2147483648, undefined);
  g("jspb.BinaryConstants.TWO_TO_32", 4294967296, undefined);
  g("jspb.BinaryConstants.TWO_TO_52", 4503599627370496, undefined);
  g("jspb.BinaryConstants.TWO_TO_63", 9223372036854776000, undefined);
  g("jspb.BinaryConstants.TWO_TO_64", 18446744073709552000, undefined);
  g("jspb.BinaryConstants.ZERO_HASH", "\x00\x00\x00\x00\x00\x00\x00\x00", undefined);
  var y = 0;
  var z = 0;
  g("jspb.utils.getSplit64Low", function() {
    return y;
  }, undefined);
  g("jspb.utils.getSplit64High", function() {
    return z;
  }, undefined);
  function Fa(a) {
    var b = a >>> 0;
    a = Math.floor((a - b) / 4294967296) >>> 0;
    y = b;
    z = a;
  }
  g("jspb.utils.splitUint64", Fa, undefined);
  function A(a) {
    var b = 0 > a;
    a = Math.abs(a);
    var c = a >>> 0;
    a = Math.floor((a - c) / 4294967296);
    a >>>= 0;
    b && (a = ~a >>> 0, c = (~c >>> 0) + 1, 4294967295 < c && (c = 0, a++, 4294967295 < a && (a = 0)));
    y = c;
    z = a;
  }
  g("jspb.utils.splitInt64", A, undefined);
  function Ga(a) {
    var b = 0 > a;
    a = 2 * Math.abs(a);
    Fa(a);
    a = y;
    var c = z;
    b && (a == 0 ? c == 0 ? c = a = 4294967295 : (c--, a = 4294967295) : a--);
    y = a;
    z = c;
  }
  g("jspb.utils.splitZigzag64", Ga, undefined);
  function Ha(a) {
    var b = 0 > a ? 1 : 0;
    a = b ? -a : a;
    if (a === 0)
      0 < 1 / a ? y = z = 0 : (z = 0, y = 2147483648);
    else if (isNaN(a))
      z = 0, y = 2147483647;
    else if (340282346638528860000000000000000000000 < a)
      z = 0, y = (b << 31 | 2139095040) >>> 0;
    else if (0.000000000000000000000000000000000000011754943508222875 > a)
      a = Math.round(a / Math.pow(2, -149)), z = 0, y = (b << 31 | a) >>> 0;
    else {
      var c = Math.floor(Math.log(a) / Math.LN2);
      a *= Math.pow(2, -c);
      a = Math.round(8388608 * a);
      16777216 <= a && ++c;
      z = 0;
      y = (b << 31 | c + 127 << 23 | a & 8388607) >>> 0;
    }
  }
  g("jspb.utils.splitFloat32", Ha, undefined);
  function Ia(a) {
    var b = 0 > a ? 1 : 0;
    a = b ? -a : a;
    if (a === 0)
      z = 0 < 1 / a ? 0 : 2147483648, y = 0;
    else if (isNaN(a))
      z = 2147483647, y = 4294967295;
    else if (179769313486231570000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000 < a)
      z = (b << 31 | 2146435072) >>> 0, y = 0;
    else if (0.000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000022250738585072014 > a)
      a /= Math.pow(2, -1074), z = (b << 31 | a / 4294967296) >>> 0, y = a >>> 0;
    else {
      var c = a, d = 0;
      if (2 <= c)
        for (;2 <= c && 1023 > d; )
          d++, c /= 2;
      else
        for (;1 > c && -1022 < d; )
          c *= 2, d--;
      a *= Math.pow(2, -d);
      z = (b << 31 | d + 1023 << 20 | 1048576 * a & 1048575) >>> 0;
      y = 4503599627370496 * a >>> 0;
    }
  }
  g("jspb.utils.splitFloat64", Ia, undefined);
  function C(a) {
    var b = a.charCodeAt(4), c = a.charCodeAt(5), d = a.charCodeAt(6), f = a.charCodeAt(7);
    y = a.charCodeAt(0) + (a.charCodeAt(1) << 8) + (a.charCodeAt(2) << 16) + (a.charCodeAt(3) << 24) >>> 0;
    z = b + (c << 8) + (d << 16) + (f << 24) >>> 0;
  }
  g("jspb.utils.splitHash64", C, undefined);
  function D(a, b) {
    return 4294967296 * b + (a >>> 0);
  }
  g("jspb.utils.joinUint64", D, undefined);
  function E(a, b) {
    var c = b & 2147483648;
    c && (a = ~a + 1 >>> 0, b = ~b >>> 0, a == 0 && (b = b + 1 >>> 0));
    a = D(a, b);
    return c ? -a : a;
  }
  g("jspb.utils.joinInt64", E, undefined);
  function Ja(a, b, c) {
    var d = b >> 31;
    return c(a << 1 ^ d, (b << 1 | a >>> 31) ^ d);
  }
  g("jspb.utils.toZigzag64", Ja, undefined);
  function Ka(a, b) {
    return Ma(a, b, E);
  }
  g("jspb.utils.joinZigzag64", Ka, undefined);
  function Ma(a, b, c) {
    var d = -(a & 1);
    return c((a >>> 1 | b << 31) ^ d, b >>> 1 ^ d);
  }
  g("jspb.utils.fromZigzag64", Ma, undefined);
  function Na(a) {
    var b = 2 * (a >> 31) + 1, c = a >>> 23 & 255;
    a &= 8388607;
    return c == 255 ? a ? NaN : Infinity * b : c == 0 ? b * Math.pow(2, -149) * a : b * Math.pow(2, c - 150) * (a + Math.pow(2, 23));
  }
  g("jspb.utils.joinFloat32", Na, undefined);
  function Oa(a, b) {
    var c = 2 * (b >> 31) + 1, d = b >>> 20 & 2047;
    a = 4294967296 * (b & 1048575) + a;
    return d == 2047 ? a ? NaN : Infinity * c : d == 0 ? c * Math.pow(2, -1074) * a : c * Math.pow(2, d - 1075) * (a + 4503599627370496);
  }
  g("jspb.utils.joinFloat64", Oa, undefined);
  function Pa(a, b) {
    return String.fromCharCode(a >>> 0 & 255, a >>> 8 & 255, a >>> 16 & 255, a >>> 24 & 255, b >>> 0 & 255, b >>> 8 & 255, b >>> 16 & 255, b >>> 24 & 255);
  }
  g("jspb.utils.joinHash64", Pa, undefined);
  g("jspb.utils.DIGITS", "0123456789abcdef".split(""), undefined);
  function F(a, b) {
    function c(f, h) {
      f = f ? String(f) : "";
      return h ? "0000000".slice(f.length) + f : f;
    }
    if (2097151 >= b)
      return "" + D(a, b);
    var d = (a >>> 24 | b << 8) >>> 0 & 16777215;
    b = b >> 16 & 65535;
    a = (a & 16777215) + 6777216 * d + 6710656 * b;
    d += 8147497 * b;
    b *= 2;
    1e7 <= a && (d += Math.floor(a / 1e7), a %= 1e7);
    1e7 <= d && (b += Math.floor(d / 1e7), d %= 1e7);
    return c(b, 0) + c(d, b) + c(a, 1);
  }
  g("jspb.utils.joinUnsignedDecimalString", F, undefined);
  function G(a, b) {
    var c = b & 2147483648;
    c && (a = ~a + 1 >>> 0, b = ~b + (a == 0 ? 1 : 0) >>> 0);
    a = F(a, b);
    return c ? "-" + a : a;
  }
  g("jspb.utils.joinSignedDecimalString", G, undefined);
  function Qa(a, b) {
    C(a);
    a = y;
    var c = z;
    return b ? G(a, c) : F(a, c);
  }
  g("jspb.utils.hash64ToDecimalString", Qa, undefined);
  g("jspb.utils.hash64ArrayToDecimalStrings", function(a, b) {
    for (var c = Array(a.length), d = 0;d < a.length; d++)
      c[d] = Qa(a[d], b);
    return c;
  }, undefined);
  function H(a) {
    function b(m, t) {
      for (var B = 0;8 > B && (m !== 1 || 0 < t); B++)
        t = m * f[B] + t, f[B] = t & 255, t >>>= 8;
    }
    function c() {
      for (var m = 0;8 > m; m++)
        f[m] = ~f[m] & 255;
    }
    n(0 < a.length);
    var d = false;
    a[0] === "-" && (d = true, a = a.slice(1));
    for (var f = [0, 0, 0, 0, 0, 0, 0, 0], h = 0;h < a.length; h++)
      b(10, a.charCodeAt(h) - 48);
    d && (c(), b(1, 1));
    return xa(f);
  }
  g("jspb.utils.decimalStringToHash64", H, undefined);
  g("jspb.utils.splitDecimalString", function(a) {
    C(H(a));
  }, undefined);
  function Ra(a) {
    return String.fromCharCode(10 > a ? 48 + a : 87 + a);
  }
  function Sa(a) {
    return 97 <= a ? a - 97 + 10 : a - 48;
  }
  g("jspb.utils.hash64ToHexString", function(a) {
    var b = Array(18);
    b[0] = "0";
    b[1] = "x";
    for (var c = 0;8 > c; c++) {
      var d = a.charCodeAt(7 - c);
      b[2 * c + 2] = Ra(d >> 4);
      b[2 * c + 3] = Ra(d & 15);
    }
    return b.join("");
  }, undefined);
  g("jspb.utils.hexStringToHash64", function(a) {
    a = a.toLowerCase();
    n(a.length == 18);
    n(a[0] == "0");
    n(a[1] == "x");
    for (var b = "", c = 0;8 > c; c++)
      b = String.fromCharCode(16 * Sa(a.charCodeAt(2 * c + 2)) + Sa(a.charCodeAt(2 * c + 3))) + b;
    return b;
  }, undefined);
  g("jspb.utils.hash64ToNumber", function(a, b) {
    C(a);
    a = y;
    var c = z;
    return b ? E(a, c) : D(a, c);
  }, undefined);
  g("jspb.utils.numberToHash64", function(a) {
    A(a);
    return Pa(y, z);
  }, undefined);
  g("jspb.utils.countVarints", function(a, b, c) {
    for (var d = 0, f = b;f < c; f++)
      d += a[f] >> 7;
    return c - b - d;
  }, undefined);
  g("jspb.utils.countVarintFields", function(a, b, c, d) {
    var f = 0;
    d *= 8;
    if (128 > d)
      for (;b < c && a[b++] == d; )
        for (f++;; ) {
          var h = a[b++];
          if ((h & 128) == 0)
            break;
        }
    else
      for (;b < c; ) {
        for (h = d;128 < h; ) {
          if (a[b] != (h & 127 | 128))
            return f;
          b++;
          h >>= 7;
        }
        if (a[b++] != h)
          break;
        for (f++;h = a[b++], (h & 128) != 0; )
          ;
      }
    return f;
  }, undefined);
  function Ta(a, b, c, d, f) {
    var h = 0;
    if (128 > d)
      for (;b < c && a[b++] == d; )
        h++, b += f;
    else
      for (;b < c; ) {
        for (var m = d;128 < m; ) {
          if (a[b++] != (m & 127 | 128))
            return h;
          m >>= 7;
        }
        if (a[b++] != m)
          break;
        h++;
        b += f;
      }
    return h;
  }
  g("jspb.utils.countFixed32Fields", function(a, b, c, d) {
    return Ta(a, b, c, 8 * d + 5, 4);
  }, undefined);
  g("jspb.utils.countFixed64Fields", function(a, b, c, d) {
    return Ta(a, b, c, 8 * d + 1, 8);
  }, undefined);
  g("jspb.utils.countDelimitedFields", function(a, b, c, d) {
    var f = 0;
    for (d = 8 * d + 2;b < c; ) {
      for (var h = d;128 < h; ) {
        if (a[b++] != (h & 127 | 128))
          return f;
        h >>= 7;
      }
      if (a[b++] != h)
        break;
      f++;
      for (var m = 0, t = 1;h = a[b++], m += (h & 127) * t, t *= 128, (h & 128) != 0; )
        ;
      b += m;
    }
    return f;
  }, undefined);
  g("jspb.utils.debugBytesToTextFormat", function(a) {
    var b = '"';
    if (a) {
      a = Ua(a);
      for (var c = 0;c < a.length; c++)
        b += "\\x", 16 > a[c] && (b += "0"), b += a[c].toString(16);
    }
    return b + '"';
  }, undefined);
  g("jspb.utils.debugScalarToTextFormat", function(a) {
    if (typeof a === "string") {
      a = String(a);
      for (var b = ['"'], c = 0;c < a.length; c++) {
        var d = a.charAt(c), f = d.charCodeAt(0), h = c + 1, m;
        if (!(m = ya[d])) {
          if (!(31 < f && 127 > f))
            if (f = d, f in za)
              d = za[f];
            else if (f in ya)
              d = za[f] = ya[f];
            else {
              m = f.charCodeAt(0);
              if (31 < m && 127 > m)
                d = f;
              else {
                if (256 > m) {
                  if (d = "\\x", 16 > m || 256 < m)
                    d += "0";
                } else
                  d = "\\u", 4096 > m && (d += "0");
                d += m.toString(16).toUpperCase();
              }
              d = za[f] = d;
            }
          m = d;
        }
        b[h] = m;
      }
      b.push('"');
      a = b.join("");
    } else
      a = a.toString();
    return a;
  }, undefined);
  g("jspb.utils.stringToByteArray", function(a) {
    for (var b = new Uint8Array(a.length), c = 0;c < a.length; c++) {
      var d = a.charCodeAt(c);
      if (255 < d)
        throw Error("Conversion error: string contains codepoint outside of byte range");
      b[c] = d;
    }
    return b;
  }, undefined);
  function Ua(a) {
    if (a.constructor === Uint8Array)
      return a;
    if (a.constructor === ArrayBuffer)
      return new Uint8Array(a);
    if (a.constructor === Array)
      return new Uint8Array(a);
    if (a.constructor === String)
      return Da(a);
    if (a instanceof Uint8Array)
      return new Uint8Array(a.buffer, a.byteOffset, a.byteLength);
    p("Type not convertible to Uint8Array.");
    return new Uint8Array(0);
  }
  g("jspb.utils.byteSourceToUint8Array", Ua, undefined);
  function I(a, b, c) {
    this.b = null;
    this.a = this.c = this.h = 0;
    this.v = false;
    a && this.H(a, b, c);
  }
  g("jspb.BinaryDecoder", I, undefined);
  var Va = [];
  I.getInstanceCacheLength = function() {
    return Va.length;
  };
  function Wa(a, b, c) {
    if (Va.length) {
      var d = Va.pop();
      a && d.H(a, b, c);
      return d;
    }
    return new I(a, b, c);
  }
  I.alloc = Wa;
  I.prototype.Ca = function() {
    this.clear();
    100 > Va.length && Va.push(this);
  };
  I.prototype.free = I.prototype.Ca;
  I.prototype.clone = function() {
    return Wa(this.b, this.h, this.c - this.h);
  };
  I.prototype.clone = I.prototype.clone;
  I.prototype.clear = function() {
    this.b = null;
    this.a = this.c = this.h = 0;
    this.v = false;
  };
  I.prototype.clear = I.prototype.clear;
  I.prototype.Y = function() {
    return this.b;
  };
  I.prototype.getBuffer = I.prototype.Y;
  I.prototype.H = function(a, b, c) {
    this.b = Ua(a);
    this.h = b !== undefined ? b : 0;
    this.c = c !== undefined ? this.h + c : this.b.length;
    this.a = this.h;
  };
  I.prototype.setBlock = I.prototype.H;
  I.prototype.Db = function() {
    return this.c;
  };
  I.prototype.getEnd = I.prototype.Db;
  I.prototype.setEnd = function(a) {
    this.c = a;
  };
  I.prototype.setEnd = I.prototype.setEnd;
  I.prototype.reset = function() {
    this.a = this.h;
  };
  I.prototype.reset = I.prototype.reset;
  I.prototype.B = function() {
    return this.a;
  };
  I.prototype.getCursor = I.prototype.B;
  I.prototype.Ma = function(a) {
    this.a = a;
  };
  I.prototype.setCursor = I.prototype.Ma;
  I.prototype.advance = function(a) {
    this.a += a;
    n(this.a <= this.c);
  };
  I.prototype.advance = I.prototype.advance;
  I.prototype.ya = function() {
    return this.a == this.c;
  };
  I.prototype.atEnd = I.prototype.ya;
  I.prototype.Qb = function() {
    return this.a > this.c;
  };
  I.prototype.pastEnd = I.prototype.Qb;
  I.prototype.getError = function() {
    return this.v || 0 > this.a || this.a > this.c;
  };
  I.prototype.getError = I.prototype.getError;
  I.prototype.w = function(a) {
    for (var b = 128, c = 0, d = 0, f = 0;4 > f && 128 <= b; f++)
      b = this.b[this.a++], c |= (b & 127) << 7 * f;
    128 <= b && (b = this.b[this.a++], c |= (b & 127) << 28, d |= (b & 127) >> 4);
    if (128 <= b)
      for (f = 0;5 > f && 128 <= b; f++)
        b = this.b[this.a++], d |= (b & 127) << 7 * f + 3;
    if (128 > b)
      return a(c >>> 0, d >>> 0);
    p("Failed to read varint, encoding is invalid.");
    this.v = true;
  };
  I.prototype.readSplitVarint64 = I.prototype.w;
  I.prototype.ea = function(a) {
    return this.w(function(b, c) {
      return Ma(b, c, a);
    });
  };
  I.prototype.readSplitZigzagVarint64 = I.prototype.ea;
  I.prototype.ta = function(a) {
    var b = this.b, c = this.a;
    this.a += 8;
    for (var d = 0, f = 0, h = c + 7;h >= c; h--)
      d = d << 8 | b[h], f = f << 8 | b[h + 4];
    return a(d, f);
  };
  I.prototype.readSplitFixed64 = I.prototype.ta;
  I.prototype.kb = function() {
    for (;this.b[this.a] & 128; )
      this.a++;
    this.a++;
  };
  I.prototype.skipVarint = I.prototype.kb;
  I.prototype.mb = function(a) {
    for (;128 < a; )
      this.a--, a >>>= 7;
    this.a--;
  };
  I.prototype.unskipVarint = I.prototype.mb;
  I.prototype.o = function() {
    var a = this.b;
    var b = a[this.a];
    var c = b & 127;
    if (128 > b)
      return this.a += 1, n(this.a <= this.c), c;
    b = a[this.a + 1];
    c |= (b & 127) << 7;
    if (128 > b)
      return this.a += 2, n(this.a <= this.c), c;
    b = a[this.a + 2];
    c |= (b & 127) << 14;
    if (128 > b)
      return this.a += 3, n(this.a <= this.c), c;
    b = a[this.a + 3];
    c |= (b & 127) << 21;
    if (128 > b)
      return this.a += 4, n(this.a <= this.c), c;
    b = a[this.a + 4];
    c |= (b & 15) << 28;
    if (128 > b)
      return this.a += 5, n(this.a <= this.c), c >>> 0;
    this.a += 5;
    128 <= a[this.a++] && 128 <= a[this.a++] && 128 <= a[this.a++] && 128 <= a[this.a++] && 128 <= a[this.a++] && n(false);
    n(this.a <= this.c);
    return c;
  };
  I.prototype.readUnsignedVarint32 = I.prototype.o;
  I.prototype.da = function() {
    return ~~this.o();
  };
  I.prototype.readSignedVarint32 = I.prototype.da;
  I.prototype.O = function() {
    return this.o().toString();
  };
  I.prototype.Ea = function() {
    return this.da().toString();
  };
  I.prototype.readSignedVarint32String = I.prototype.Ea;
  I.prototype.Ia = function() {
    var a = this.o();
    return a >>> 1 ^ -(a & 1);
  };
  I.prototype.readZigzagVarint32 = I.prototype.Ia;
  I.prototype.Ga = function() {
    return this.w(D);
  };
  I.prototype.readUnsignedVarint64 = I.prototype.Ga;
  I.prototype.Ha = function() {
    return this.w(F);
  };
  I.prototype.readUnsignedVarint64String = I.prototype.Ha;
  I.prototype.sa = function() {
    return this.w(E);
  };
  I.prototype.readSignedVarint64 = I.prototype.sa;
  I.prototype.Fa = function() {
    return this.w(G);
  };
  I.prototype.readSignedVarint64String = I.prototype.Fa;
  I.prototype.Ja = function() {
    return this.w(Ka);
  };
  I.prototype.readZigzagVarint64 = I.prototype.Ja;
  I.prototype.fb = function() {
    return this.ea(Pa);
  };
  I.prototype.readZigzagVarintHash64 = I.prototype.fb;
  I.prototype.Ka = function() {
    return this.ea(G);
  };
  I.prototype.readZigzagVarint64String = I.prototype.Ka;
  I.prototype.Gc = function() {
    var a = this.b[this.a];
    this.a += 1;
    n(this.a <= this.c);
    return a;
  };
  I.prototype.readUint8 = I.prototype.Gc;
  I.prototype.Ec = function() {
    var a = this.b[this.a], b = this.b[this.a + 1];
    this.a += 2;
    n(this.a <= this.c);
    return a << 0 | b << 8;
  };
  I.prototype.readUint16 = I.prototype.Ec;
  I.prototype.m = function() {
    var a = this.b[this.a], b = this.b[this.a + 1], c = this.b[this.a + 2], d = this.b[this.a + 3];
    this.a += 4;
    n(this.a <= this.c);
    return (a << 0 | b << 8 | c << 16 | d << 24) >>> 0;
  };
  I.prototype.readUint32 = I.prototype.m;
  I.prototype.ga = function() {
    var a = this.m(), b = this.m();
    return D(a, b);
  };
  I.prototype.readUint64 = I.prototype.ga;
  I.prototype.ha = function() {
    var a = this.m(), b = this.m();
    return F(a, b);
  };
  I.prototype.readUint64String = I.prototype.ha;
  I.prototype.Xb = function() {
    var a = this.b[this.a];
    this.a += 1;
    n(this.a <= this.c);
    return a << 24 >> 24;
  };
  I.prototype.readInt8 = I.prototype.Xb;
  I.prototype.Vb = function() {
    var a = this.b[this.a], b = this.b[this.a + 1];
    this.a += 2;
    n(this.a <= this.c);
    return (a << 0 | b << 8) << 16 >> 16;
  };
  I.prototype.readInt16 = I.prototype.Vb;
  I.prototype.P = function() {
    var a = this.b[this.a], b = this.b[this.a + 1], c = this.b[this.a + 2], d = this.b[this.a + 3];
    this.a += 4;
    n(this.a <= this.c);
    return a << 0 | b << 8 | c << 16 | d << 24;
  };
  I.prototype.readInt32 = I.prototype.P;
  I.prototype.ba = function() {
    var a = this.m(), b = this.m();
    return E(a, b);
  };
  I.prototype.readInt64 = I.prototype.ba;
  I.prototype.ca = function() {
    var a = this.m(), b = this.m();
    return G(a, b);
  };
  I.prototype.readInt64String = I.prototype.ca;
  I.prototype.aa = function() {
    var a = this.m();
    return Na(a, 0);
  };
  I.prototype.readFloat = I.prototype.aa;
  I.prototype.Z = function() {
    var a = this.m(), b = this.m();
    return Oa(a, b);
  };
  I.prototype.readDouble = I.prototype.Z;
  I.prototype.pa = function() {
    return !!this.b[this.a++];
  };
  I.prototype.readBool = I.prototype.pa;
  I.prototype.ra = function() {
    return this.da();
  };
  I.prototype.readEnum = I.prototype.ra;
  I.prototype.fa = function(a) {
    var b = this.b, c = this.a;
    a = c + a;
    for (var d = [], f = "";c < a; ) {
      var h = b[c++];
      if (128 > h)
        d.push(h);
      else if (192 > h)
        continue;
      else if (224 > h) {
        var m = b[c++];
        d.push((h & 31) << 6 | m & 63);
      } else if (240 > h) {
        m = b[c++];
        var t = b[c++];
        d.push((h & 15) << 12 | (m & 63) << 6 | t & 63);
      } else if (248 > h) {
        m = b[c++];
        t = b[c++];
        var B = b[c++];
        h = (h & 7) << 18 | (m & 63) << 12 | (t & 63) << 6 | B & 63;
        h -= 65536;
        d.push((h >> 10 & 1023) + 55296, (h & 1023) + 56320);
      }
      8192 <= d.length && (f += String.fromCharCode.apply(null, d), d.length = 0);
    }
    f += xa(d);
    this.a = c;
    return f;
  };
  I.prototype.readString = I.prototype.fa;
  I.prototype.Dc = function() {
    var a = this.o();
    return this.fa(a);
  };
  I.prototype.readStringWithLength = I.prototype.Dc;
  I.prototype.qa = function(a) {
    if (0 > a || this.a + a > this.b.length)
      return this.v = true, p("Invalid byte length!"), new Uint8Array(0);
    var b = this.b.subarray(this.a, this.a + a);
    this.a += a;
    n(this.a <= this.c);
    return b;
  };
  I.prototype.readBytes = I.prototype.qa;
  I.prototype.ia = function() {
    return this.w(Pa);
  };
  I.prototype.readVarintHash64 = I.prototype.ia;
  I.prototype.$ = function() {
    var a = this.b, b = this.a, c = a[b], d = a[b + 1], f = a[b + 2], h = a[b + 3], m = a[b + 4], t = a[b + 5], B = a[b + 6];
    a = a[b + 7];
    this.a += 8;
    return String.fromCharCode(c, d, f, h, m, t, B, a);
  };
  I.prototype.readFixedHash64 = I.prototype.$;
  function J(a, b, c) {
    this.a = Wa(a, b, c);
    this.O = this.a.B();
    this.b = this.c = -1;
    this.h = false;
    this.v = null;
  }
  g("jspb.BinaryReader", J, undefined);
  var K = [];
  J.clearInstanceCache = function() {
    K = [];
  };
  J.getInstanceCacheLength = function() {
    return K.length;
  };
  function Xa(a, b, c) {
    if (K.length) {
      var d = K.pop();
      a && d.a.H(a, b, c);
      return d;
    }
    return new J(a, b, c);
  }
  J.alloc = Xa;
  J.prototype.zb = Xa;
  J.prototype.alloc = J.prototype.zb;
  J.prototype.Ca = function() {
    this.a.clear();
    this.b = this.c = -1;
    this.h = false;
    this.v = null;
    100 > K.length && K.push(this);
  };
  J.prototype.free = J.prototype.Ca;
  J.prototype.Fb = function() {
    return this.O;
  };
  J.prototype.getFieldCursor = J.prototype.Fb;
  J.prototype.B = function() {
    return this.a.B();
  };
  J.prototype.getCursor = J.prototype.B;
  J.prototype.Y = function() {
    return this.a.Y();
  };
  J.prototype.getBuffer = J.prototype.Y;
  J.prototype.Hb = function() {
    return this.c;
  };
  J.prototype.getFieldNumber = J.prototype.Hb;
  J.prototype.Lb = function() {
    return this.b;
  };
  J.prototype.getWireType = J.prototype.Lb;
  J.prototype.Mb = function() {
    return this.b == 2;
  };
  J.prototype.isDelimited = J.prototype.Mb;
  J.prototype.bb = function() {
    return this.b == 4;
  };
  J.prototype.isEndGroup = J.prototype.bb;
  J.prototype.getError = function() {
    return this.h || this.a.getError();
  };
  J.prototype.getError = J.prototype.getError;
  J.prototype.H = function(a, b, c) {
    this.a.H(a, b, c);
    this.b = this.c = -1;
  };
  J.prototype.setBlock = J.prototype.H;
  J.prototype.reset = function() {
    this.a.reset();
    this.b = this.c = -1;
  };
  J.prototype.reset = J.prototype.reset;
  J.prototype.advance = function(a) {
    this.a.advance(a);
  };
  J.prototype.advance = J.prototype.advance;
  J.prototype.oa = function() {
    if (this.a.ya())
      return false;
    if (this.getError())
      return p("Decoder hit an error"), false;
    this.O = this.a.B();
    var a = this.a.o(), b = a >>> 3;
    a &= 7;
    if (a != 0 && a != 5 && a != 1 && a != 2 && a != 3 && a != 4)
      return p("Invalid wire type: %s (at position %s)", a, this.O), this.h = true, false;
    this.c = b;
    this.b = a;
    return true;
  };
  J.prototype.nextField = J.prototype.oa;
  J.prototype.Oa = function() {
    this.a.mb(this.c << 3 | this.b);
  };
  J.prototype.unskipHeader = J.prototype.Oa;
  J.prototype.Lc = function() {
    var a = this.c;
    for (this.Oa();this.oa() && this.c == a; )
      this.C();
    this.a.ya() || this.Oa();
  };
  J.prototype.skipMatchingFields = J.prototype.Lc;
  J.prototype.lb = function() {
    this.b != 0 ? (p("Invalid wire type for skipVarintField"), this.C()) : this.a.kb();
  };
  J.prototype.skipVarintField = J.prototype.lb;
  J.prototype.gb = function() {
    if (this.b != 2)
      p("Invalid wire type for skipDelimitedField"), this.C();
    else {
      var a = this.a.o();
      this.a.advance(a);
    }
  };
  J.prototype.skipDelimitedField = J.prototype.gb;
  J.prototype.hb = function() {
    this.b != 5 ? (p("Invalid wire type for skipFixed32Field"), this.C()) : this.a.advance(4);
  };
  J.prototype.skipFixed32Field = J.prototype.hb;
  J.prototype.ib = function() {
    this.b != 1 ? (p("Invalid wire type for skipFixed64Field"), this.C()) : this.a.advance(8);
  };
  J.prototype.skipFixed64Field = J.prototype.ib;
  J.prototype.jb = function() {
    var a = this.c;
    do {
      if (!this.oa()) {
        p("Unmatched start-group tag: stream EOF");
        this.h = true;
        break;
      }
      if (this.b == 4) {
        this.c != a && (p("Unmatched end-group tag"), this.h = true);
        break;
      }
      this.C();
    } while (1);
  };
  J.prototype.skipGroup = J.prototype.jb;
  J.prototype.C = function() {
    switch (this.b) {
      case 0:
        this.lb();
        break;
      case 1:
        this.ib();
        break;
      case 2:
        this.gb();
        break;
      case 5:
        this.hb();
        break;
      case 3:
        this.jb();
        break;
      default:
        p("Invalid wire encoding for field.");
    }
  };
  J.prototype.skipField = J.prototype.C;
  J.prototype.Hc = function(a, b) {
    this.v === null && (this.v = {});
    n(!this.v[a]);
    this.v[a] = b;
  };
  J.prototype.registerReadCallback = J.prototype.Hc;
  J.prototype.Ic = function(a) {
    n(this.v !== null);
    a = this.v[a];
    n(a);
    return a(this);
  };
  J.prototype.runReadCallback = J.prototype.Ic;
  J.prototype.Yb = function(a, b) {
    n(this.b == 2);
    var c = this.a.c, d = this.a.o();
    d = this.a.B() + d;
    this.a.setEnd(d);
    b(a, this);
    this.a.Ma(d);
    this.a.setEnd(c);
  };
  J.prototype.readMessage = J.prototype.Yb;
  J.prototype.Ub = function(a, b, c) {
    n(this.b == 3);
    n(this.c == a);
    c(b, this);
    this.h || this.b == 4 || (p("Group submessage did not end with an END_GROUP tag"), this.h = true);
  };
  J.prototype.readGroup = J.prototype.Ub;
  J.prototype.Gb = function() {
    n(this.b == 2);
    var a = this.a.o(), b = this.a.B(), c = b + a;
    a = Wa(this.a.Y(), b, a);
    this.a.Ma(c);
    return a;
  };
  J.prototype.getFieldDecoder = J.prototype.Gb;
  J.prototype.P = function() {
    n(this.b == 0);
    return this.a.da();
  };
  J.prototype.readInt32 = J.prototype.P;
  J.prototype.Wb = function() {
    n(this.b == 0);
    return this.a.Ea();
  };
  J.prototype.readInt32String = J.prototype.Wb;
  J.prototype.ba = function() {
    n(this.b == 0);
    return this.a.sa();
  };
  J.prototype.readInt64 = J.prototype.ba;
  J.prototype.ca = function() {
    n(this.b == 0);
    return this.a.Fa();
  };
  J.prototype.readInt64String = J.prototype.ca;
  J.prototype.m = function() {
    n(this.b == 0);
    return this.a.o();
  };
  J.prototype.readUint32 = J.prototype.m;
  J.prototype.Fc = function() {
    n(this.b == 0);
    return this.a.O();
  };
  J.prototype.readUint32String = J.prototype.Fc;
  J.prototype.ga = function() {
    n(this.b == 0);
    return this.a.Ga();
  };
  J.prototype.readUint64 = J.prototype.ga;
  J.prototype.ha = function() {
    n(this.b == 0);
    return this.a.Ha();
  };
  J.prototype.readUint64String = J.prototype.ha;
  J.prototype.zc = function() {
    n(this.b == 0);
    return this.a.Ia();
  };
  J.prototype.readSint32 = J.prototype.zc;
  J.prototype.Ac = function() {
    n(this.b == 0);
    return this.a.Ja();
  };
  J.prototype.readSint64 = J.prototype.Ac;
  J.prototype.Bc = function() {
    n(this.b == 0);
    return this.a.Ka();
  };
  J.prototype.readSint64String = J.prototype.Bc;
  J.prototype.Rb = function() {
    n(this.b == 5);
    return this.a.m();
  };
  J.prototype.readFixed32 = J.prototype.Rb;
  J.prototype.Sb = function() {
    n(this.b == 1);
    return this.a.ga();
  };
  J.prototype.readFixed64 = J.prototype.Sb;
  J.prototype.Tb = function() {
    n(this.b == 1);
    return this.a.ha();
  };
  J.prototype.readFixed64String = J.prototype.Tb;
  J.prototype.vc = function() {
    n(this.b == 5);
    return this.a.P();
  };
  J.prototype.readSfixed32 = J.prototype.vc;
  J.prototype.wc = function() {
    n(this.b == 5);
    return this.a.P().toString();
  };
  J.prototype.readSfixed32String = J.prototype.wc;
  J.prototype.xc = function() {
    n(this.b == 1);
    return this.a.ba();
  };
  J.prototype.readSfixed64 = J.prototype.xc;
  J.prototype.yc = function() {
    n(this.b == 1);
    return this.a.ca();
  };
  J.prototype.readSfixed64String = J.prototype.yc;
  J.prototype.aa = function() {
    n(this.b == 5);
    return this.a.aa();
  };
  J.prototype.readFloat = J.prototype.aa;
  J.prototype.Z = function() {
    n(this.b == 1);
    return this.a.Z();
  };
  J.prototype.readDouble = J.prototype.Z;
  J.prototype.pa = function() {
    n(this.b == 0);
    return !!this.a.o();
  };
  J.prototype.readBool = J.prototype.pa;
  J.prototype.ra = function() {
    n(this.b == 0);
    return this.a.sa();
  };
  J.prototype.readEnum = J.prototype.ra;
  J.prototype.fa = function() {
    n(this.b == 2);
    var a = this.a.o();
    return this.a.fa(a);
  };
  J.prototype.readString = J.prototype.fa;
  J.prototype.qa = function() {
    n(this.b == 2);
    var a = this.a.o();
    return this.a.qa(a);
  };
  J.prototype.readBytes = J.prototype.qa;
  J.prototype.ia = function() {
    n(this.b == 0);
    return this.a.ia();
  };
  J.prototype.readVarintHash64 = J.prototype.ia;
  J.prototype.Cc = function() {
    n(this.b == 0);
    return this.a.fb();
  };
  J.prototype.readSintHash64 = J.prototype.Cc;
  J.prototype.w = function(a) {
    n(this.b == 0);
    return this.a.w(a);
  };
  J.prototype.readSplitVarint64 = J.prototype.w;
  J.prototype.ea = function(a) {
    n(this.b == 0);
    return this.a.w(function(b, c) {
      return Ma(b, c, a);
    });
  };
  J.prototype.readSplitZigzagVarint64 = J.prototype.ea;
  J.prototype.$ = function() {
    n(this.b == 1);
    return this.a.$();
  };
  J.prototype.readFixedHash64 = J.prototype.$;
  J.prototype.ta = function(a) {
    n(this.b == 1);
    return this.a.ta(a);
  };
  J.prototype.readSplitFixed64 = J.prototype.ta;
  function L(a, b) {
    n(a.b == 2);
    var c = a.a.o();
    c = a.a.B() + c;
    for (var d = [];a.a.B() < c; )
      d.push(b.call(a.a));
    return d;
  }
  J.prototype.gc = function() {
    return L(this, this.a.da);
  };
  J.prototype.readPackedInt32 = J.prototype.gc;
  J.prototype.hc = function() {
    return L(this, this.a.Ea);
  };
  J.prototype.readPackedInt32String = J.prototype.hc;
  J.prototype.ic = function() {
    return L(this, this.a.sa);
  };
  J.prototype.readPackedInt64 = J.prototype.ic;
  J.prototype.jc = function() {
    return L(this, this.a.Fa);
  };
  J.prototype.readPackedInt64String = J.prototype.jc;
  J.prototype.qc = function() {
    return L(this, this.a.o);
  };
  J.prototype.readPackedUint32 = J.prototype.qc;
  J.prototype.rc = function() {
    return L(this, this.a.O);
  };
  J.prototype.readPackedUint32String = J.prototype.rc;
  J.prototype.sc = function() {
    return L(this, this.a.Ga);
  };
  J.prototype.readPackedUint64 = J.prototype.sc;
  J.prototype.tc = function() {
    return L(this, this.a.Ha);
  };
  J.prototype.readPackedUint64String = J.prototype.tc;
  J.prototype.nc = function() {
    return L(this, this.a.Ia);
  };
  J.prototype.readPackedSint32 = J.prototype.nc;
  J.prototype.oc = function() {
    return L(this, this.a.Ja);
  };
  J.prototype.readPackedSint64 = J.prototype.oc;
  J.prototype.pc = function() {
    return L(this, this.a.Ka);
  };
  J.prototype.readPackedSint64String = J.prototype.pc;
  J.prototype.bc = function() {
    return L(this, this.a.m);
  };
  J.prototype.readPackedFixed32 = J.prototype.bc;
  J.prototype.cc = function() {
    return L(this, this.a.ga);
  };
  J.prototype.readPackedFixed64 = J.prototype.cc;
  J.prototype.dc = function() {
    return L(this, this.a.ha);
  };
  J.prototype.readPackedFixed64String = J.prototype.dc;
  J.prototype.kc = function() {
    return L(this, this.a.P);
  };
  J.prototype.readPackedSfixed32 = J.prototype.kc;
  J.prototype.lc = function() {
    return L(this, this.a.ba);
  };
  J.prototype.readPackedSfixed64 = J.prototype.lc;
  J.prototype.mc = function() {
    return L(this, this.a.ca);
  };
  J.prototype.readPackedSfixed64String = J.prototype.mc;
  J.prototype.fc = function() {
    return L(this, this.a.aa);
  };
  J.prototype.readPackedFloat = J.prototype.fc;
  J.prototype.$b = function() {
    return L(this, this.a.Z);
  };
  J.prototype.readPackedDouble = J.prototype.$b;
  J.prototype.Zb = function() {
    return L(this, this.a.pa);
  };
  J.prototype.readPackedBool = J.prototype.Zb;
  J.prototype.ac = function() {
    return L(this, this.a.ra);
  };
  J.prototype.readPackedEnum = J.prototype.ac;
  J.prototype.uc = function() {
    return L(this, this.a.ia);
  };
  J.prototype.readPackedVarintHash64 = J.prototype.uc;
  J.prototype.ec = function() {
    return L(this, this.a.$);
  };
  J.prototype.readPackedFixedHash64 = J.prototype.ec;
  function Ya(a, b, c, d, f) {
    this.ma = a;
    this.Ba = b;
    this.la = c;
    this.Na = d;
    this.na = f;
  }
  g("jspb.ExtensionFieldInfo", Ya, undefined);
  function Za(a, b, c, d, f, h) {
    this.Za = a;
    this.za = b;
    this.Aa = c;
    this.Wa = d;
    this.Ab = f;
    this.Nb = h;
  }
  g("jspb.ExtensionFieldBinaryInfo", Za, undefined);
  Ya.prototype.F = function() {
    return !!this.la;
  };
  Ya.prototype.isMessageType = Ya.prototype.F;
  function N() {}
  g("jspb.Message", N, undefined);
  N.GENERATE_TO_OBJECT = true;
  N.GENERATE_FROM_OBJECT = true;
  var $a = typeof Uint8Array == "function";
  N.prototype.Ib = function() {
    return this.b;
  };
  N.prototype.getJsPbMessageId = N.prototype.Ib;
  N.initialize = function(a, b, c, d, f, h) {
    a.f = null;
    b || (b = c ? [c] : []);
    a.b = c ? String(c) : undefined;
    a.D = c === 0 ? -1 : 0;
    a.u = b;
    a: {
      c = a.u.length;
      b = -1;
      if (c && (b = c - 1, c = a.u[b], !(c === null || typeof c != "object" || Array.isArray(c) || $a && c instanceof Uint8Array))) {
        a.G = b - a.D;
        a.i = c;
        break a;
      }
      -1 < d ? (a.G = Math.max(d, b + 1 - a.D), a.i = null) : a.G = Number.MAX_VALUE;
    }
    a.a = {};
    if (f)
      for (d = 0;d < f.length; d++)
        b = f[d], b < a.G ? (b += a.D, a.u[b] = a.u[b] || ab) : (bb(a), a.i[b] = a.i[b] || ab);
    if (h && h.length)
      for (d = 0;d < h.length; d++)
        cb(a, h[d]);
  };
  var ab = Object.freeze ? Object.freeze([]) : [];
  function bb(a) {
    var b = a.G + a.D;
    a.u[b] || (a.i = a.u[b] = {});
  }
  function db(a, b, c) {
    for (var d = [], f = 0;f < a.length; f++)
      d[f] = b.call(a[f], c, a[f]);
    return d;
  }
  N.toObjectList = db;
  N.toObjectExtension = function(a, b, c, d, f) {
    for (var h in c) {
      var m = c[h], t = d.call(a, m);
      if (t != null) {
        for (var B in m.Ba)
          if (m.Ba.hasOwnProperty(B))
            break;
        b[B] = m.Na ? m.na ? db(t, m.Na, f) : m.Na(f, t) : t;
      }
    }
  };
  N.serializeBinaryExtensions = function(a, b, c, d) {
    for (var f in c) {
      var h = c[f], m = h.Za;
      if (!h.Aa)
        throw Error("Message extension present that was generated without binary serialization support");
      var t = d.call(a, m);
      if (t != null)
        if (m.F())
          if (h.Wa)
            h.Aa.call(b, m.ma, t, h.Wa);
          else
            throw Error("Message extension present holding submessage without binary support enabled, and message is being serialized to binary format");
        else
          h.Aa.call(b, m.ma, t);
    }
  };
  N.readBinaryExtension = function(a, b, c, d, f) {
    var h = c[b.c];
    if (h) {
      c = h.Za;
      if (!h.za)
        throw Error("Deserializing extension whose generated code does not support binary format");
      if (c.F()) {
        var m = new c.la;
        h.za.call(b, m, h.Ab);
      } else
        m = h.za.call(b);
      c.na && !h.Nb ? (b = d.call(a, c)) ? b.push(m) : f.call(a, c, [m]) : f.call(a, c, m);
    } else
      b.C();
  };
  function O(a, b) {
    if (b < a.G) {
      b += a.D;
      var c = a.u[b];
      return c === ab ? a.u[b] = [] : c;
    }
    if (a.i)
      return c = a.i[b], c === ab ? a.i[b] = [] : c;
  }
  N.getField = O;
  N.getRepeatedField = function(a, b) {
    return O(a, b);
  };
  function eb(a, b) {
    a = O(a, b);
    return a == null ? a : +a;
  }
  N.getOptionalFloatingPointField = eb;
  function fb(a, b) {
    a = O(a, b);
    return a == null ? a : !!a;
  }
  N.getBooleanField = fb;
  N.getRepeatedFloatingPointField = function(a, b) {
    var c = O(a, b);
    a.a || (a.a = {});
    if (!a.a[b]) {
      for (var d = 0;d < c.length; d++)
        c[d] = +c[d];
      a.a[b] = true;
    }
    return c;
  };
  N.getRepeatedBooleanField = function(a, b) {
    var c = O(a, b);
    a.a || (a.a = {});
    if (!a.a[b]) {
      for (var d = 0;d < c.length; d++)
        c[d] = !!c[d];
      a.a[b] = true;
    }
    return c;
  };
  function gb(a) {
    if (a == null || typeof a === "string")
      return a;
    if ($a && a instanceof Uint8Array)
      return Ba(a);
    p("Cannot coerce to b64 string: " + k(a));
    return null;
  }
  N.bytesAsB64 = gb;
  function hb(a) {
    if (a == null || a instanceof Uint8Array)
      return a;
    if (typeof a === "string")
      return Da(a);
    p("Cannot coerce to Uint8Array: " + k(a));
    return null;
  }
  N.bytesAsU8 = hb;
  N.bytesListAsB64 = function(a) {
    ib(a);
    return a.length && typeof a[0] !== "string" ? l(a, gb) : a;
  };
  N.bytesListAsU8 = function(a) {
    ib(a);
    return !a.length || a[0] instanceof Uint8Array ? a : l(a, hb);
  };
  function ib(a) {
    if (a && 1 < a.length) {
      var b = k(a[0]);
      qa(a, function(c) {
        k(c) != b && p("Inconsistent type in JSPB repeated field array. Got " + k(c) + " expected " + b);
      });
    }
  }
  function jb(a, b, c) {
    a = O(a, b);
    return a == null ? c : a;
  }
  N.getFieldWithDefault = jb;
  N.getBooleanFieldWithDefault = function(a, b, c) {
    a = fb(a, b);
    return a == null ? c : a;
  };
  N.getFloatingPointFieldWithDefault = function(a, b, c) {
    a = eb(a, b);
    return a == null ? c : a;
  };
  N.getFieldProto3 = jb;
  N.getMapField = function(a, b, c, d) {
    a.f || (a.f = {});
    if (b in a.f)
      return a.f[b];
    var f = O(a, b);
    if (!f) {
      if (c)
        return;
      f = [];
      P(a, b, f);
    }
    return a.f[b] = new r(f, d);
  };
  function P(a, b, c) {
    q(a, N);
    b < a.G ? a.u[b + a.D] = c : (bb(a), a.i[b] = c);
    return a;
  }
  N.setField = P;
  N.setProto3IntField = function(a, b, c) {
    return Q(a, b, c, 0);
  };
  N.setProto3FloatField = function(a, b, c) {
    return Q(a, b, c, 0);
  };
  N.setProto3BooleanField = function(a, b, c) {
    return Q(a, b, c, false);
  };
  N.setProto3StringField = function(a, b, c) {
    return Q(a, b, c, "");
  };
  N.setProto3BytesField = function(a, b, c) {
    return Q(a, b, c, "");
  };
  N.setProto3EnumField = function(a, b, c) {
    return Q(a, b, c, 0);
  };
  N.setProto3StringIntField = function(a, b, c) {
    return Q(a, b, c, "0");
  };
  function Q(a, b, c, d) {
    q(a, N);
    c !== d ? P(a, b, c) : b < a.G ? a.u[b + a.D] = null : (bb(a), delete a.i[b]);
    return a;
  }
  N.addToRepeatedField = function(a, b, c, d) {
    q(a, N);
    b = O(a, b);
    d != null ? b.splice(d, 0, c) : b.push(c);
    return a;
  };
  function kb(a, b, c, d) {
    q(a, N);
    (c = cb(a, c)) && c !== b && d !== undefined && (a.f && (c in a.f) && (a.f[c] = undefined), P(a, c, undefined));
    return P(a, b, d);
  }
  N.setOneofField = kb;
  function cb(a, b) {
    for (var c, d, f = 0;f < b.length; f++) {
      var h = b[f], m = O(a, h);
      m != null && (c = h, d = m, P(a, h, undefined));
    }
    return c ? (P(a, c, d), c) : 0;
  }
  N.computeOneofCase = cb;
  N.getWrapperField = function(a, b, c, d) {
    a.f || (a.f = {});
    if (!a.f[c]) {
      var f = O(a, c);
      if (d || f)
        a.f[c] = new b(f);
    }
    return a.f[c];
  };
  N.getRepeatedWrapperField = function(a, b, c) {
    lb(a, b, c);
    b = a.f[c];
    b == ab && (b = a.f[c] = []);
    return b;
  };
  function lb(a, b, c) {
    a.f || (a.f = {});
    if (!a.f[c]) {
      for (var d = O(a, c), f = [], h = 0;h < d.length; h++)
        f[h] = new b(d[h]);
      a.f[c] = f;
    }
  }
  N.setWrapperField = function(a, b, c) {
    q(a, N);
    a.f || (a.f = {});
    var d = c ? c.g() : c;
    a.f[b] = c;
    return P(a, b, d);
  };
  N.setOneofWrapperField = function(a, b, c, d) {
    q(a, N);
    a.f || (a.f = {});
    var f = d ? d.g() : d;
    a.f[b] = d;
    return kb(a, b, c, f);
  };
  N.setRepeatedWrapperField = function(a, b, c) {
    q(a, N);
    a.f || (a.f = {});
    c = c || [];
    for (var d = [], f = 0;f < c.length; f++)
      d[f] = c[f].g();
    a.f[b] = c;
    return P(a, b, d);
  };
  N.addToRepeatedWrapperField = function(a, b, c, d, f) {
    lb(a, d, b);
    var h = a.f[b];
    h || (h = a.f[b] = []);
    c = c ? c : new d;
    a = O(a, b);
    f != null ? (h.splice(f, 0, c), a.splice(f, 0, c.g())) : (h.push(c), a.push(c.g()));
    return c;
  };
  N.toMap = function(a, b, c, d) {
    for (var f = {}, h = 0;h < a.length; h++)
      f[b.call(a[h])] = c ? c.call(a[h], d, a[h]) : a[h];
    return f;
  };
  function mb(a) {
    if (a.f)
      for (var b in a.f) {
        var c = a.f[b];
        if (Array.isArray(c))
          for (var d = 0;d < c.length; d++)
            c[d] && c[d].g();
        else
          c && c.g();
      }
  }
  N.prototype.g = function() {
    mb(this);
    return this.u;
  };
  N.prototype.toArray = N.prototype.g;
  N.prototype.toString = function() {
    mb(this);
    return this.u.toString();
  };
  N.prototype.getExtension = function(a) {
    if (this.i) {
      this.f || (this.f = {});
      var b = a.ma;
      if (a.na) {
        if (a.F())
          return this.f[b] || (this.f[b] = l(this.i[b] || [], function(c) {
            return new a.la(c);
          })), this.f[b];
      } else if (a.F())
        return !this.f[b] && this.i[b] && (this.f[b] = new a.la(this.i[b])), this.f[b];
      return this.i[b];
    }
  };
  N.prototype.getExtension = N.prototype.getExtension;
  N.prototype.Kc = function(a, b) {
    this.f || (this.f = {});
    bb(this);
    var c = a.ma;
    a.na ? (b = b || [], a.F() ? (this.f[c] = b, this.i[c] = l(b, function(d) {
      return d.g();
    })) : this.i[c] = b) : a.F() ? (this.f[c] = b, this.i[c] = b ? b.g() : b) : this.i[c] = b;
    return this;
  };
  N.prototype.setExtension = N.prototype.Kc;
  N.difference = function(a, b) {
    if (!(a instanceof b.constructor))
      throw Error("Messages have different types.");
    var c = a.g();
    b = b.g();
    var d = [], f = 0, h = c.length > b.length ? c.length : b.length;
    a.b && (d[0] = a.b, f = 1);
    for (;f < h; f++)
      nb(c[f], b[f]) || (d[f] = b[f]);
    return new a.constructor(d);
  };
  N.equals = function(a, b) {
    return a == b || !(!a || !b) && a instanceof b.constructor && nb(a.g(), b.g());
  };
  function ob(a, b) {
    a = a || {};
    b = b || {};
    var c = {}, d;
    for (d in a)
      c[d] = 0;
    for (d in b)
      c[d] = 0;
    for (d in c)
      if (!nb(a[d], b[d]))
        return false;
    return true;
  }
  N.compareExtensions = ob;
  function nb(a, b) {
    if (a == b)
      return true;
    if (!la(a) || !la(b))
      return typeof a === "number" && isNaN(a) || typeof b === "number" && isNaN(b) ? String(a) == String(b) : false;
    if (a.constructor != b.constructor)
      return false;
    if ($a && a.constructor === Uint8Array) {
      if (a.length != b.length)
        return false;
      for (var c = 0;c < a.length; c++)
        if (a[c] != b[c])
          return false;
      return true;
    }
    if (a.constructor === Array) {
      var d = undefined, f = undefined, h = Math.max(a.length, b.length);
      for (c = 0;c < h; c++) {
        var m = a[c], t = b[c];
        m && m.constructor == Object && (n(d === undefined), n(c === a.length - 1), d = m, m = undefined);
        t && t.constructor == Object && (n(f === undefined), n(c === b.length - 1), f = t, t = undefined);
        if (!nb(m, t))
          return false;
      }
      return d || f ? (d = d || {}, f = f || {}, ob(d, f)) : true;
    }
    if (a.constructor === Object)
      return ob(a, b);
    throw Error("Invalid type in JSPB array");
  }
  N.compareFields = nb;
  N.prototype.Bb = function() {
    return pb(this);
  };
  N.prototype.cloneMessage = N.prototype.Bb;
  N.prototype.clone = function() {
    return pb(this);
  };
  N.prototype.clone = N.prototype.clone;
  N.clone = function(a) {
    return pb(a);
  };
  function pb(a) {
    return new a.constructor(qb(a.g()));
  }
  N.copyInto = function(a, b) {
    q(a, N);
    q(b, N);
    n(a.constructor == b.constructor, "Copy source and target message should have the same type.");
    a = pb(a);
    for (var c = b.g(), d = a.g(), f = c.length = 0;f < d.length; f++)
      c[f] = d[f];
    b.f = a.f;
    b.i = a.i;
  };
  function qb(a) {
    if (Array.isArray(a)) {
      for (var b = Array(a.length), c = 0;c < a.length; c++) {
        var d = a[c];
        d != null && (b[c] = typeof d == "object" ? qb(n(d)) : d);
      }
      return b;
    }
    if ($a && a instanceof Uint8Array)
      return new Uint8Array(a);
    b = {};
    for (c in a)
      d = a[c], d != null && (b[c] = typeof d == "object" ? qb(n(d)) : d);
    return b;
  }
  N.registerMessageType = function(a, b) {
    b.we = a;
  };
  var R = { dump: function(a) {
    q(a, N, "jspb.Message instance expected");
    n(a.getExtension, "Only unobfuscated and unoptimized compilation modes supported.");
    return R.X(a);
  } };
  g("jspb.debug.dump", R.dump, undefined);
  R.X = function(a) {
    var b = k(a);
    if (b == "number" || b == "string" || b == "boolean" || b == "null" || b == "undefined" || typeof Uint8Array !== "undefined" && a instanceof Uint8Array)
      return a;
    if (b == "array")
      return ua(a), l(a, R.X);
    if (a instanceof r) {
      var c = {};
      a = a.entries();
      for (var d = a.next();!d.done; d = a.next())
        c[d.value[0]] = R.X(d.value[1]);
      return c;
    }
    q(a, N, "Only messages expected: " + a);
    b = a.constructor;
    var f = { $name: b.name || b.displayName };
    for (t in b.prototype) {
      var h = /^get([A-Z]\w*)/.exec(t);
      if (h && t != "getExtension" && t != "getJsPbMessageId") {
        var m = "has" + h[1];
        if (!a[m] || a[m]())
          m = a[t](), f[R.$a(h[1])] = R.X(m);
      }
    }
    if (a.extensionObject_)
      return f.$extensions = "Recursive dumping of extensions not supported in compiled code. Switch to uncompiled or dump extension object directly", f;
    for (d in b.extensions)
      if (/^\d+$/.test(d)) {
        m = b.extensions[d];
        var t = a.getExtension(m);
        h = undefined;
        m = m.Ba;
        var B = [], M = 0;
        for (h in m)
          B[M++] = h;
        h = B[0];
        t != null && (c || (c = f.$extensions = {}), c[R.$a(h)] = R.X(t));
      }
    return f;
  };
  R.$a = function(a) {
    return a.replace(/^[A-Z]/, function(b) {
      return b.toLowerCase();
    });
  };
  function S() {
    this.a = [];
  }
  g("jspb.BinaryEncoder", S, undefined);
  S.prototype.length = function() {
    return this.a.length;
  };
  S.prototype.length = S.prototype.length;
  S.prototype.end = function() {
    var a = this.a;
    this.a = [];
    return a;
  };
  S.prototype.end = S.prototype.end;
  S.prototype.l = function(a, b) {
    n(a == Math.floor(a));
    n(b == Math.floor(b));
    n(0 <= a && 4294967296 > a);
    for (n(0 <= b && 4294967296 > b);0 < b || 127 < a; )
      this.a.push(a & 127 | 128), a = (a >>> 7 | b << 25) >>> 0, b >>>= 7;
    this.a.push(a);
  };
  S.prototype.writeSplitVarint64 = S.prototype.l;
  S.prototype.A = function(a, b) {
    n(a == Math.floor(a));
    n(b == Math.floor(b));
    n(0 <= a && 4294967296 > a);
    n(0 <= b && 4294967296 > b);
    this.s(a);
    this.s(b);
  };
  S.prototype.writeSplitFixed64 = S.prototype.A;
  S.prototype.j = function(a) {
    n(a == Math.floor(a));
    for (n(0 <= a && 4294967296 > a);127 < a; )
      this.a.push(a & 127 | 128), a >>>= 7;
    this.a.push(a);
  };
  S.prototype.writeUnsignedVarint32 = S.prototype.j;
  S.prototype.M = function(a) {
    n(a == Math.floor(a));
    n(-2147483648 <= a && 2147483648 > a);
    if (0 <= a)
      this.j(a);
    else {
      for (var b = 0;9 > b; b++)
        this.a.push(a & 127 | 128), a >>= 7;
      this.a.push(1);
    }
  };
  S.prototype.writeSignedVarint32 = S.prototype.M;
  S.prototype.va = function(a) {
    n(a == Math.floor(a));
    n(0 <= a && 18446744073709552000 > a);
    A(a);
    this.l(y, z);
  };
  S.prototype.writeUnsignedVarint64 = S.prototype.va;
  S.prototype.ua = function(a) {
    n(a == Math.floor(a));
    n(-9223372036854776000 <= a && 9223372036854776000 > a);
    A(a);
    this.l(y, z);
  };
  S.prototype.writeSignedVarint64 = S.prototype.ua;
  S.prototype.wa = function(a) {
    n(a == Math.floor(a));
    n(-2147483648 <= a && 2147483648 > a);
    this.j((a << 1 ^ a >> 31) >>> 0);
  };
  S.prototype.writeZigzagVarint32 = S.prototype.wa;
  S.prototype.xa = function(a) {
    n(a == Math.floor(a));
    n(-9223372036854776000 <= a && 9223372036854776000 > a);
    Ga(a);
    this.l(y, z);
  };
  S.prototype.writeZigzagVarint64 = S.prototype.xa;
  S.prototype.Ta = function(a) {
    this.W(H(a));
  };
  S.prototype.writeZigzagVarint64String = S.prototype.Ta;
  S.prototype.W = function(a) {
    var b = this;
    C(a);
    Ja(y, z, function(c, d) {
      b.l(c >>> 0, d >>> 0);
    });
  };
  S.prototype.writeZigzagVarintHash64 = S.prototype.W;
  S.prototype.be = function(a) {
    n(a == Math.floor(a));
    n(0 <= a && 256 > a);
    this.a.push(a >>> 0 & 255);
  };
  S.prototype.writeUint8 = S.prototype.be;
  S.prototype.ae = function(a) {
    n(a == Math.floor(a));
    n(0 <= a && 65536 > a);
    this.a.push(a >>> 0 & 255);
    this.a.push(a >>> 8 & 255);
  };
  S.prototype.writeUint16 = S.prototype.ae;
  S.prototype.s = function(a) {
    n(a == Math.floor(a));
    n(0 <= a && 4294967296 > a);
    this.a.push(a >>> 0 & 255);
    this.a.push(a >>> 8 & 255);
    this.a.push(a >>> 16 & 255);
    this.a.push(a >>> 24 & 255);
  };
  S.prototype.writeUint32 = S.prototype.s;
  S.prototype.V = function(a) {
    n(a == Math.floor(a));
    n(0 <= a && 18446744073709552000 > a);
    Fa(a);
    this.s(y);
    this.s(z);
  };
  S.prototype.writeUint64 = S.prototype.V;
  S.prototype.Qc = function(a) {
    n(a == Math.floor(a));
    n(-128 <= a && 128 > a);
    this.a.push(a >>> 0 & 255);
  };
  S.prototype.writeInt8 = S.prototype.Qc;
  S.prototype.Pc = function(a) {
    n(a == Math.floor(a));
    n(-32768 <= a && 32768 > a);
    this.a.push(a >>> 0 & 255);
    this.a.push(a >>> 8 & 255);
  };
  S.prototype.writeInt16 = S.prototype.Pc;
  S.prototype.S = function(a) {
    n(a == Math.floor(a));
    n(-2147483648 <= a && 2147483648 > a);
    this.a.push(a >>> 0 & 255);
    this.a.push(a >>> 8 & 255);
    this.a.push(a >>> 16 & 255);
    this.a.push(a >>> 24 & 255);
  };
  S.prototype.writeInt32 = S.prototype.S;
  S.prototype.T = function(a) {
    n(a == Math.floor(a));
    n(-9223372036854776000 <= a && 9223372036854776000 > a);
    A(a);
    this.A(y, z);
  };
  S.prototype.writeInt64 = S.prototype.T;
  S.prototype.ka = function(a) {
    n(a == Math.floor(a));
    n(-9223372036854776000 <= +a && 9223372036854776000 > +a);
    C(H(a));
    this.A(y, z);
  };
  S.prototype.writeInt64String = S.prototype.ka;
  S.prototype.L = function(a) {
    n(a === Infinity || a === -Infinity || isNaN(a) || -340282346638528860000000000000000000000 <= a && 340282346638528860000000000000000000000 >= a);
    Ha(a);
    this.s(y);
  };
  S.prototype.writeFloat = S.prototype.L;
  S.prototype.J = function(a) {
    n(a === Infinity || a === -Infinity || isNaN(a) || -179769313486231570000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000 <= a && 179769313486231570000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000 >= a);
    Ia(a);
    this.s(y);
    this.s(z);
  };
  S.prototype.writeDouble = S.prototype.J;
  S.prototype.I = function(a) {
    n(typeof a === "boolean" || typeof a === "number");
    this.a.push(a ? 1 : 0);
  };
  S.prototype.writeBool = S.prototype.I;
  S.prototype.R = function(a) {
    n(a == Math.floor(a));
    n(-2147483648 <= a && 2147483648 > a);
    this.M(a);
  };
  S.prototype.writeEnum = S.prototype.R;
  S.prototype.ja = function(a) {
    this.a.push.apply(this.a, a);
  };
  S.prototype.writeBytes = S.prototype.ja;
  S.prototype.N = function(a) {
    C(a);
    this.l(y, z);
  };
  S.prototype.writeVarintHash64 = S.prototype.N;
  S.prototype.K = function(a) {
    C(a);
    this.s(y);
    this.s(z);
  };
  S.prototype.writeFixedHash64 = S.prototype.K;
  S.prototype.U = function(a) {
    var b = this.a.length;
    ta(a);
    for (var c = 0;c < a.length; c++) {
      var d = a.charCodeAt(c);
      if (128 > d)
        this.a.push(d);
      else if (2048 > d)
        this.a.push(d >> 6 | 192), this.a.push(d & 63 | 128);
      else if (65536 > d)
        if (55296 <= d && 56319 >= d && c + 1 < a.length) {
          var f = a.charCodeAt(c + 1);
          56320 <= f && 57343 >= f && (d = 1024 * (d - 55296) + f - 56320 + 65536, this.a.push(d >> 18 | 240), this.a.push(d >> 12 & 63 | 128), this.a.push(d >> 6 & 63 | 128), this.a.push(d & 63 | 128), c++);
        } else
          this.a.push(d >> 12 | 224), this.a.push(d >> 6 & 63 | 128), this.a.push(d & 63 | 128);
    }
    return this.a.length - b;
  };
  S.prototype.writeString = S.prototype.U;
  function T(a, b) {
    this.lo = a;
    this.hi = b;
  }
  g("jspb.arith.UInt64", T, undefined);
  T.prototype.cmp = function(a) {
    return this.hi < a.hi || this.hi == a.hi && this.lo < a.lo ? -1 : this.hi == a.hi && this.lo == a.lo ? 0 : 1;
  };
  T.prototype.cmp = T.prototype.cmp;
  T.prototype.La = function() {
    return new T((this.lo >>> 1 | (this.hi & 1) << 31) >>> 0, this.hi >>> 1 >>> 0);
  };
  T.prototype.rightShift = T.prototype.La;
  T.prototype.Da = function() {
    return new T(this.lo << 1 >>> 0, (this.hi << 1 | this.lo >>> 31) >>> 0);
  };
  T.prototype.leftShift = T.prototype.Da;
  T.prototype.cb = function() {
    return !!(this.hi & 2147483648);
  };
  T.prototype.msb = T.prototype.cb;
  T.prototype.Ob = function() {
    return !!(this.lo & 1);
  };
  T.prototype.lsb = T.prototype.Ob;
  T.prototype.Ua = function() {
    return this.lo == 0 && this.hi == 0;
  };
  T.prototype.zero = T.prototype.Ua;
  T.prototype.add = function(a) {
    return new T((this.lo + a.lo & 4294967295) >>> 0 >>> 0, ((this.hi + a.hi & 4294967295) >>> 0) + (4294967296 <= this.lo + a.lo ? 1 : 0) >>> 0);
  };
  T.prototype.add = T.prototype.add;
  T.prototype.sub = function(a) {
    return new T((this.lo - a.lo & 4294967295) >>> 0 >>> 0, ((this.hi - a.hi & 4294967295) >>> 0) - (0 > this.lo - a.lo ? 1 : 0) >>> 0);
  };
  T.prototype.sub = T.prototype.sub;
  function rb(a, b) {
    var c = a & 65535;
    a >>>= 16;
    var d = b & 65535, f = b >>> 16;
    b = c * d + 65536 * (c * f & 65535) + 65536 * (a * d & 65535);
    for (c = a * f + (c * f >>> 16) + (a * d >>> 16);4294967296 <= b; )
      b -= 4294967296, c += 1;
    return new T(b >>> 0, c >>> 0);
  }
  T.mul32x32 = rb;
  T.prototype.eb = function(a) {
    var b = rb(this.lo, a);
    a = rb(this.hi, a);
    a.hi = a.lo;
    a.lo = 0;
    return b.add(a);
  };
  T.prototype.mul = T.prototype.eb;
  T.prototype.Xa = function(a) {
    if (a == 0)
      return [];
    var b = new T(0, 0), c = new T(this.lo, this.hi);
    a = new T(a, 0);
    for (var d = new T(1, 0);!a.cb(); )
      a = a.Da(), d = d.Da();
    for (;!d.Ua(); )
      0 >= a.cmp(c) && (b = b.add(d), c = c.sub(a)), a = a.La(), d = d.La();
    return [b, c];
  };
  T.prototype.div = T.prototype.Xa;
  T.prototype.toString = function() {
    for (var a = "", b = this;!b.Ua(); ) {
      b = b.Xa(10);
      var c = b[0];
      a = b[1].lo + a;
      b = c;
    }
    a == "" && (a = "0");
    return a;
  };
  T.prototype.toString = T.prototype.toString;
  function U(a) {
    for (var b = new T(0, 0), c = new T(0, 0), d = 0;d < a.length; d++) {
      if ("0" > a[d] || "9" < a[d])
        return null;
      c.lo = parseInt(a[d], 10);
      b = b.eb(10).add(c);
    }
    return b;
  }
  T.fromString = U;
  T.prototype.clone = function() {
    return new T(this.lo, this.hi);
  };
  T.prototype.clone = T.prototype.clone;
  function V(a, b) {
    this.lo = a;
    this.hi = b;
  }
  g("jspb.arith.Int64", V, undefined);
  V.prototype.add = function(a) {
    return new V((this.lo + a.lo & 4294967295) >>> 0 >>> 0, ((this.hi + a.hi & 4294967295) >>> 0) + (4294967296 <= this.lo + a.lo ? 1 : 0) >>> 0);
  };
  V.prototype.add = V.prototype.add;
  V.prototype.sub = function(a) {
    return new V((this.lo - a.lo & 4294967295) >>> 0 >>> 0, ((this.hi - a.hi & 4294967295) >>> 0) - (0 > this.lo - a.lo ? 1 : 0) >>> 0);
  };
  V.prototype.sub = V.prototype.sub;
  V.prototype.clone = function() {
    return new V(this.lo, this.hi);
  };
  V.prototype.clone = V.prototype.clone;
  V.prototype.toString = function() {
    var a = (this.hi & 2147483648) != 0, b = new T(this.lo, this.hi);
    a && (b = new T(0, 0).sub(b));
    return (a ? "-" : "") + b.toString();
  };
  V.prototype.toString = V.prototype.toString;
  function sb(a) {
    var b = 0 < a.length && a[0] == "-";
    b && (a = a.substring(1));
    a = U(a);
    if (a === null)
      return null;
    b && (a = new T(0, 0).sub(a));
    return new V(a.lo, a.hi);
  }
  V.fromString = sb;
  function W() {
    this.c = [];
    this.b = 0;
    this.a = new S;
    this.h = [];
  }
  g("jspb.BinaryWriter", W, undefined);
  function tb(a, b) {
    var c = a.a.end();
    a.c.push(c);
    a.c.push(b);
    a.b += c.length + b.length;
  }
  function X(a, b) {
    Y(a, b, 2);
    b = a.a.end();
    a.c.push(b);
    a.b += b.length;
    b.push(a.b);
    return b;
  }
  function Z(a, b) {
    var c = b.pop();
    c = a.b + a.a.length() - c;
    for (n(0 <= c);127 < c; )
      b.push(c & 127 | 128), c >>>= 7, a.b++;
    b.push(c);
    a.b++;
  }
  W.prototype.pb = function(a, b, c) {
    tb(this, a.subarray(b, c));
  };
  W.prototype.writeSerializedMessage = W.prototype.pb;
  W.prototype.Pb = function(a, b, c) {
    a != null && b != null && c != null && this.pb(a, b, c);
  };
  W.prototype.maybeWriteSerializedMessage = W.prototype.Pb;
  W.prototype.reset = function() {
    this.c = [];
    this.a.end();
    this.b = 0;
    this.h = [];
  };
  W.prototype.reset = W.prototype.reset;
  W.prototype.ab = function() {
    n(this.h.length == 0);
    for (var a = new Uint8Array(this.b + this.a.length()), b = this.c, c = b.length, d = 0, f = 0;f < c; f++) {
      var h = b[f];
      a.set(h, d);
      d += h.length;
    }
    b = this.a.end();
    a.set(b, d);
    d += b.length;
    n(d == a.length);
    this.c = [a];
    return a;
  };
  W.prototype.getResultBuffer = W.prototype.ab;
  W.prototype.Kb = function(a) {
    return Ba(this.ab(), a);
  };
  W.prototype.getResultBase64String = W.prototype.Kb;
  W.prototype.Va = function(a) {
    this.h.push(X(this, a));
  };
  W.prototype.beginSubMessage = W.prototype.Va;
  W.prototype.Ya = function() {
    n(0 <= this.h.length);
    Z(this, this.h.pop());
  };
  W.prototype.endSubMessage = W.prototype.Ya;
  function Y(a, b, c) {
    n(1 <= b && b == Math.floor(b));
    a.a.j(8 * b + c);
  }
  W.prototype.Nc = function(a, b, c) {
    switch (a) {
      case 1:
        this.J(b, c);
        break;
      case 2:
        this.L(b, c);
        break;
      case 3:
        this.T(b, c);
        break;
      case 4:
        this.V(b, c);
        break;
      case 5:
        this.S(b, c);
        break;
      case 6:
        this.Qa(b, c);
        break;
      case 7:
        this.Pa(b, c);
        break;
      case 8:
        this.I(b, c);
        break;
      case 9:
        this.U(b, c);
        break;
      case 10:
        p("Group field type not supported in writeAny()");
        break;
      case 11:
        p("Message field type not supported in writeAny()");
        break;
      case 12:
        this.ja(b, c);
        break;
      case 13:
        this.s(b, c);
        break;
      case 14:
        this.R(b, c);
        break;
      case 15:
        this.Ra(b, c);
        break;
      case 16:
        this.Sa(b, c);
        break;
      case 17:
        this.rb(b, c);
        break;
      case 18:
        this.sb(b, c);
        break;
      case 30:
        this.K(b, c);
        break;
      case 31:
        this.N(b, c);
        break;
      default:
        p("Invalid field type in writeAny()");
    }
  };
  W.prototype.writeAny = W.prototype.Nc;
  function ub(a, b, c) {
    c != null && (Y(a, b, 0), a.a.j(c));
  }
  function vb(a, b, c) {
    c != null && (Y(a, b, 0), a.a.M(c));
  }
  W.prototype.S = function(a, b) {
    b != null && (n(-2147483648 <= b && 2147483648 > b), vb(this, a, b));
  };
  W.prototype.writeInt32 = W.prototype.S;
  W.prototype.ob = function(a, b) {
    b != null && (b = parseInt(b, 10), n(-2147483648 <= b && 2147483648 > b), vb(this, a, b));
  };
  W.prototype.writeInt32String = W.prototype.ob;
  W.prototype.T = function(a, b) {
    b != null && (n(-9223372036854776000 <= b && 9223372036854776000 > b), b != null && (Y(this, a, 0), this.a.ua(b)));
  };
  W.prototype.writeInt64 = W.prototype.T;
  W.prototype.ka = function(a, b) {
    b != null && (b = sb(b), Y(this, a, 0), this.a.l(b.lo, b.hi));
  };
  W.prototype.writeInt64String = W.prototype.ka;
  W.prototype.s = function(a, b) {
    b != null && (n(0 <= b && 4294967296 > b), ub(this, a, b));
  };
  W.prototype.writeUint32 = W.prototype.s;
  W.prototype.ub = function(a, b) {
    b != null && (b = parseInt(b, 10), n(0 <= b && 4294967296 > b), ub(this, a, b));
  };
  W.prototype.writeUint32String = W.prototype.ub;
  W.prototype.V = function(a, b) {
    b != null && (n(0 <= b && 18446744073709552000 > b), b != null && (Y(this, a, 0), this.a.va(b)));
  };
  W.prototype.writeUint64 = W.prototype.V;
  W.prototype.vb = function(a, b) {
    b != null && (b = U(b), Y(this, a, 0), this.a.l(b.lo, b.hi));
  };
  W.prototype.writeUint64String = W.prototype.vb;
  W.prototype.rb = function(a, b) {
    b != null && (n(-2147483648 <= b && 2147483648 > b), b != null && (Y(this, a, 0), this.a.wa(b)));
  };
  W.prototype.writeSint32 = W.prototype.rb;
  W.prototype.sb = function(a, b) {
    b != null && (n(-9223372036854776000 <= b && 9223372036854776000 > b), b != null && (Y(this, a, 0), this.a.xa(b)));
  };
  W.prototype.writeSint64 = W.prototype.sb;
  W.prototype.$d = function(a, b) {
    b != null && b != null && (Y(this, a, 0), this.a.W(b));
  };
  W.prototype.writeSintHash64 = W.prototype.$d;
  W.prototype.Zd = function(a, b) {
    b != null && b != null && (Y(this, a, 0), this.a.Ta(b));
  };
  W.prototype.writeSint64String = W.prototype.Zd;
  W.prototype.Pa = function(a, b) {
    b != null && (n(0 <= b && 4294967296 > b), Y(this, a, 5), this.a.s(b));
  };
  W.prototype.writeFixed32 = W.prototype.Pa;
  W.prototype.Qa = function(a, b) {
    b != null && (n(0 <= b && 18446744073709552000 > b), Y(this, a, 1), this.a.V(b));
  };
  W.prototype.writeFixed64 = W.prototype.Qa;
  W.prototype.nb = function(a, b) {
    b != null && (b = U(b), Y(this, a, 1), this.a.A(b.lo, b.hi));
  };
  W.prototype.writeFixed64String = W.prototype.nb;
  W.prototype.Ra = function(a, b) {
    b != null && (n(-2147483648 <= b && 2147483648 > b), Y(this, a, 5), this.a.S(b));
  };
  W.prototype.writeSfixed32 = W.prototype.Ra;
  W.prototype.Sa = function(a, b) {
    b != null && (n(-9223372036854776000 <= b && 9223372036854776000 > b), Y(this, a, 1), this.a.T(b));
  };
  W.prototype.writeSfixed64 = W.prototype.Sa;
  W.prototype.qb = function(a, b) {
    b != null && (b = sb(b), Y(this, a, 1), this.a.A(b.lo, b.hi));
  };
  W.prototype.writeSfixed64String = W.prototype.qb;
  W.prototype.L = function(a, b) {
    b != null && (Y(this, a, 5), this.a.L(b));
  };
  W.prototype.writeFloat = W.prototype.L;
  W.prototype.J = function(a, b) {
    b != null && (Y(this, a, 1), this.a.J(b));
  };
  W.prototype.writeDouble = W.prototype.J;
  W.prototype.I = function(a, b) {
    b != null && (n(typeof b === "boolean" || typeof b === "number"), Y(this, a, 0), this.a.I(b));
  };
  W.prototype.writeBool = W.prototype.I;
  W.prototype.R = function(a, b) {
    b != null && (n(-2147483648 <= b && 2147483648 > b), Y(this, a, 0), this.a.M(b));
  };
  W.prototype.writeEnum = W.prototype.R;
  W.prototype.U = function(a, b) {
    b != null && (a = X(this, a), this.a.U(b), Z(this, a));
  };
  W.prototype.writeString = W.prototype.U;
  W.prototype.ja = function(a, b) {
    b != null && (b = Ua(b), Y(this, a, 2), this.a.j(b.length), tb(this, b));
  };
  W.prototype.writeBytes = W.prototype.ja;
  W.prototype.Rc = function(a, b, c) {
    b != null && (a = X(this, a), c(b, this), Z(this, a));
  };
  W.prototype.writeMessage = W.prototype.Rc;
  W.prototype.Sc = function(a, b, c) {
    b != null && (Y(this, 1, 3), Y(this, 2, 0), this.a.M(a), a = X(this, 3), c(b, this), Z(this, a), Y(this, 1, 4));
  };
  W.prototype.writeMessageSet = W.prototype.Sc;
  W.prototype.Oc = function(a, b, c) {
    b != null && (Y(this, a, 3), c(b, this), Y(this, a, 4));
  };
  W.prototype.writeGroup = W.prototype.Oc;
  W.prototype.K = function(a, b) {
    b != null && (n(b.length == 8), Y(this, a, 1), this.a.K(b));
  };
  W.prototype.writeFixedHash64 = W.prototype.K;
  W.prototype.N = function(a, b) {
    b != null && (n(b.length == 8), Y(this, a, 0), this.a.N(b));
  };
  W.prototype.writeVarintHash64 = W.prototype.N;
  W.prototype.A = function(a, b, c) {
    Y(this, a, 1);
    this.a.A(b, c);
  };
  W.prototype.writeSplitFixed64 = W.prototype.A;
  W.prototype.l = function(a, b, c) {
    Y(this, a, 0);
    this.a.l(b, c);
  };
  W.prototype.writeSplitVarint64 = W.prototype.l;
  W.prototype.tb = function(a, b, c) {
    Y(this, a, 0);
    var d = this.a;
    Ja(b, c, function(f, h) {
      d.l(f >>> 0, h >>> 0);
    });
  };
  W.prototype.writeSplitZigzagVarint64 = W.prototype.tb;
  W.prototype.Ed = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        vb(this, a, b[c]);
  };
  W.prototype.writeRepeatedInt32 = W.prototype.Ed;
  W.prototype.Fd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.ob(a, b[c]);
  };
  W.prototype.writeRepeatedInt32String = W.prototype.Fd;
  W.prototype.Gd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++) {
        var d = b[c];
        d != null && (Y(this, a, 0), this.a.ua(d));
      }
  };
  W.prototype.writeRepeatedInt64 = W.prototype.Gd;
  W.prototype.Qd = function(a, b, c, d) {
    if (b != null)
      for (var f = 0;f < b.length; f++)
        this.A(a, c(b[f]), d(b[f]));
  };
  W.prototype.writeRepeatedSplitFixed64 = W.prototype.Qd;
  W.prototype.Rd = function(a, b, c, d) {
    if (b != null)
      for (var f = 0;f < b.length; f++)
        this.l(a, c(b[f]), d(b[f]));
  };
  W.prototype.writeRepeatedSplitVarint64 = W.prototype.Rd;
  W.prototype.Sd = function(a, b, c, d) {
    if (b != null)
      for (var f = 0;f < b.length; f++)
        this.tb(a, c(b[f]), d(b[f]));
  };
  W.prototype.writeRepeatedSplitZigzagVarint64 = W.prototype.Sd;
  W.prototype.Hd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.ka(a, b[c]);
  };
  W.prototype.writeRepeatedInt64String = W.prototype.Hd;
  W.prototype.Ud = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        ub(this, a, b[c]);
  };
  W.prototype.writeRepeatedUint32 = W.prototype.Ud;
  W.prototype.Vd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.ub(a, b[c]);
  };
  W.prototype.writeRepeatedUint32String = W.prototype.Vd;
  W.prototype.Wd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++) {
        var d = b[c];
        d != null && (Y(this, a, 0), this.a.va(d));
      }
  };
  W.prototype.writeRepeatedUint64 = W.prototype.Wd;
  W.prototype.Xd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.vb(a, b[c]);
  };
  W.prototype.writeRepeatedUint64String = W.prototype.Xd;
  W.prototype.Md = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++) {
        var d = b[c];
        d != null && (Y(this, a, 0), this.a.wa(d));
      }
  };
  W.prototype.writeRepeatedSint32 = W.prototype.Md;
  W.prototype.Nd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++) {
        var d = b[c];
        d != null && (Y(this, a, 0), this.a.xa(d));
      }
  };
  W.prototype.writeRepeatedSint64 = W.prototype.Nd;
  W.prototype.Od = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++) {
        var d = b[c];
        d != null && (Y(this, a, 0), this.a.Ta(d));
      }
  };
  W.prototype.writeRepeatedSint64String = W.prototype.Od;
  W.prototype.Pd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++) {
        var d = b[c];
        d != null && (Y(this, a, 0), this.a.W(d));
      }
  };
  W.prototype.writeRepeatedSintHash64 = W.prototype.Pd;
  W.prototype.yd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.Pa(a, b[c]);
  };
  W.prototype.writeRepeatedFixed32 = W.prototype.yd;
  W.prototype.zd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.Qa(a, b[c]);
  };
  W.prototype.writeRepeatedFixed64 = W.prototype.zd;
  W.prototype.Ad = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.nb(a, b[c]);
  };
  W.prototype.writeRepeatedFixed64String = W.prototype.Ad;
  W.prototype.Jd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.Ra(a, b[c]);
  };
  W.prototype.writeRepeatedSfixed32 = W.prototype.Jd;
  W.prototype.Kd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.Sa(a, b[c]);
  };
  W.prototype.writeRepeatedSfixed64 = W.prototype.Kd;
  W.prototype.Ld = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.qb(a, b[c]);
  };
  W.prototype.writeRepeatedSfixed64String = W.prototype.Ld;
  W.prototype.Cd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.L(a, b[c]);
  };
  W.prototype.writeRepeatedFloat = W.prototype.Cd;
  W.prototype.wd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.J(a, b[c]);
  };
  W.prototype.writeRepeatedDouble = W.prototype.wd;
  W.prototype.ud = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.I(a, b[c]);
  };
  W.prototype.writeRepeatedBool = W.prototype.ud;
  W.prototype.xd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.R(a, b[c]);
  };
  W.prototype.writeRepeatedEnum = W.prototype.xd;
  W.prototype.Td = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.U(a, b[c]);
  };
  W.prototype.writeRepeatedString = W.prototype.Td;
  W.prototype.vd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.ja(a, b[c]);
  };
  W.prototype.writeRepeatedBytes = W.prototype.vd;
  W.prototype.Id = function(a, b, c) {
    if (b != null)
      for (var d = 0;d < b.length; d++) {
        var f = X(this, a);
        c(b[d], this);
        Z(this, f);
      }
  };
  W.prototype.writeRepeatedMessage = W.prototype.Id;
  W.prototype.Dd = function(a, b, c) {
    if (b != null)
      for (var d = 0;d < b.length; d++)
        Y(this, a, 3), c(b[d], this), Y(this, a, 4);
  };
  W.prototype.writeRepeatedGroup = W.prototype.Dd;
  W.prototype.Bd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.K(a, b[c]);
  };
  W.prototype.writeRepeatedFixedHash64 = W.prototype.Bd;
  W.prototype.Yd = function(a, b) {
    if (b != null)
      for (var c = 0;c < b.length; c++)
        this.N(a, b[c]);
  };
  W.prototype.writeRepeatedVarintHash64 = W.prototype.Yd;
  W.prototype.ad = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.M(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedInt32 = W.prototype.ad;
  W.prototype.bd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.M(parseInt(b[c], 10));
      Z(this, a);
    }
  };
  W.prototype.writePackedInt32String = W.prototype.bd;
  W.prototype.cd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.ua(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedInt64 = W.prototype.cd;
  W.prototype.md = function(a, b, c, d) {
    if (b != null) {
      a = X(this, a);
      for (var f = 0;f < b.length; f++)
        this.a.A(c(b[f]), d(b[f]));
      Z(this, a);
    }
  };
  W.prototype.writePackedSplitFixed64 = W.prototype.md;
  W.prototype.nd = function(a, b, c, d) {
    if (b != null) {
      a = X(this, a);
      for (var f = 0;f < b.length; f++)
        this.a.l(c(b[f]), d(b[f]));
      Z(this, a);
    }
  };
  W.prototype.writePackedSplitVarint64 = W.prototype.nd;
  W.prototype.od = function(a, b, c, d) {
    if (b != null) {
      a = X(this, a);
      for (var f = this.a, h = 0;h < b.length; h++)
        Ja(c(b[h]), d(b[h]), function(m, t) {
          f.l(m >>> 0, t >>> 0);
        });
      Z(this, a);
    }
  };
  W.prototype.writePackedSplitZigzagVarint64 = W.prototype.od;
  W.prototype.dd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++) {
        var d = sb(b[c]);
        this.a.l(d.lo, d.hi);
      }
      Z(this, a);
    }
  };
  W.prototype.writePackedInt64String = W.prototype.dd;
  W.prototype.pd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.j(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedUint32 = W.prototype.pd;
  W.prototype.qd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.j(parseInt(b[c], 10));
      Z(this, a);
    }
  };
  W.prototype.writePackedUint32String = W.prototype.qd;
  W.prototype.rd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.va(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedUint64 = W.prototype.rd;
  W.prototype.sd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++) {
        var d = U(b[c]);
        this.a.l(d.lo, d.hi);
      }
      Z(this, a);
    }
  };
  W.prototype.writePackedUint64String = W.prototype.sd;
  W.prototype.hd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.wa(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedSint32 = W.prototype.hd;
  W.prototype.jd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.xa(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedSint64 = W.prototype.jd;
  W.prototype.kd = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.W(H(b[c]));
      Z(this, a);
    }
  };
  W.prototype.writePackedSint64String = W.prototype.kd;
  W.prototype.ld = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.W(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedSintHash64 = W.prototype.ld;
  W.prototype.Wc = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(4 * b.length), a = 0;a < b.length; a++)
        this.a.s(b[a]);
  };
  W.prototype.writePackedFixed32 = W.prototype.Wc;
  W.prototype.Xc = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(8 * b.length), a = 0;a < b.length; a++)
        this.a.V(b[a]);
  };
  W.prototype.writePackedFixed64 = W.prototype.Xc;
  W.prototype.Yc = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(8 * b.length), a = 0;a < b.length; a++) {
        var c = U(b[a]);
        this.a.A(c.lo, c.hi);
      }
  };
  W.prototype.writePackedFixed64String = W.prototype.Yc;
  W.prototype.ed = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(4 * b.length), a = 0;a < b.length; a++)
        this.a.S(b[a]);
  };
  W.prototype.writePackedSfixed32 = W.prototype.ed;
  W.prototype.fd = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(8 * b.length), a = 0;a < b.length; a++)
        this.a.T(b[a]);
  };
  W.prototype.writePackedSfixed64 = W.prototype.fd;
  W.prototype.gd = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(8 * b.length), a = 0;a < b.length; a++)
        this.a.ka(b[a]);
  };
  W.prototype.writePackedSfixed64String = W.prototype.gd;
  W.prototype.$c = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(4 * b.length), a = 0;a < b.length; a++)
        this.a.L(b[a]);
  };
  W.prototype.writePackedFloat = W.prototype.$c;
  W.prototype.Uc = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(8 * b.length), a = 0;a < b.length; a++)
        this.a.J(b[a]);
  };
  W.prototype.writePackedDouble = W.prototype.Uc;
  W.prototype.Tc = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(b.length), a = 0;a < b.length; a++)
        this.a.I(b[a]);
  };
  W.prototype.writePackedBool = W.prototype.Tc;
  W.prototype.Vc = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.R(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedEnum = W.prototype.Vc;
  W.prototype.Zc = function(a, b) {
    if (b != null && b.length)
      for (Y(this, a, 2), this.a.j(8 * b.length), a = 0;a < b.length; a++)
        this.a.K(b[a]);
  };
  W.prototype.writePackedFixedHash64 = W.prototype.Zc;
  W.prototype.td = function(a, b) {
    if (b != null && b.length) {
      a = X(this, a);
      for (var c = 0;c < b.length; c++)
        this.a.N(b[c]);
      Z(this, a);
    }
  };
  W.prototype.writePackedVarintHash64 = W.prototype.td;
  typeof exports === "object" && (exports.debug = R, exports.Map = r, exports.Message = N, exports.BinaryReader = J, exports.BinaryWriter = W, exports.ExtensionFieldInfo = Ya, exports.ExtensionFieldBinaryInfo = Za, exports.exportSymbol = ma, exports.inherits = na, exports.object = { extend: pa }, exports.typeOf = k);
});

// node_modules/google-protobuf/google/protobuf/timestamp_pb.js
var require_timestamp_pb = __commonJS(function(exports) {
  var jspb = require_google_protobuf();
  var goog = jspb;
  var global2 = typeof globalThis !== "undefined" && globalThis || typeof window !== "undefined" && window || typeof global2 !== "undefined" && global2 || typeof self !== "undefined" && self || function() {
    return this;
  }.call(null) || Function("return this")();
  goog.exportSymbol("proto.google.protobuf.Timestamp", null, global2);
  proto.google.protobuf.Timestamp = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.google.protobuf.Timestamp, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.google.protobuf.Timestamp.displayName = "proto.google.protobuf.Timestamp";
  }
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.google.protobuf.Timestamp.prototype.toObject = function(opt_includeInstance) {
      return proto.google.protobuf.Timestamp.toObject(opt_includeInstance, this);
    };
    proto.google.protobuf.Timestamp.toObject = function(includeInstance, msg) {
      var f, obj = {
        seconds: jspb.Message.getFieldWithDefault(msg, 1, 0),
        nanos: jspb.Message.getFieldWithDefault(msg, 2, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.google.protobuf.Timestamp.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.google.protobuf.Timestamp;
    return proto.google.protobuf.Timestamp.deserializeBinaryFromReader(msg, reader);
  };
  proto.google.protobuf.Timestamp.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readInt64();
          msg.setSeconds(value);
          break;
        case 2:
          var value = reader.readInt32();
          msg.setNanos(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.google.protobuf.Timestamp.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.google.protobuf.Timestamp.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.google.protobuf.Timestamp.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getSeconds();
    if (f !== 0) {
      writer.writeInt64(1, f);
    }
    f = message.getNanos();
    if (f !== 0) {
      writer.writeInt32(2, f);
    }
  };
  proto.google.protobuf.Timestamp.prototype.getSeconds = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.google.protobuf.Timestamp.prototype.setSeconds = function(value) {
    return jspb.Message.setProto3IntField(this, 1, value);
  };
  proto.google.protobuf.Timestamp.prototype.getNanos = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.google.protobuf.Timestamp.prototype.setNanos = function(value) {
    return jspb.Message.setProto3IntField(this, 2, value);
  };
  goog.object.extend(exports, proto.google.protobuf);
  proto.google.protobuf.Timestamp.prototype.toDate = function() {
    var seconds = this.getSeconds();
    var nanos = this.getNanos();
    return new Date(seconds * 1000 + nanos / 1e6);
  };
  proto.google.protobuf.Timestamp.prototype.fromDate = function(value) {
    this.setSeconds(Math.floor(value.getTime() / 1000));
    this.setNanos(value.getMilliseconds() * 1e6);
  };
  proto.google.protobuf.Timestamp.fromDate = function(value) {
    var timestamp = new proto.google.protobuf.Timestamp;
    timestamp.fromDate(value);
    return timestamp;
  };
});

// src/generated/telemetry_pb.js
var require_telemetry_pb = __commonJS(function(exports) {
  var jspb = require_google_protobuf();
  var goog = jspb;
  var global2 = typeof globalThis !== "undefined" && globalThis || typeof window !== "undefined" && window || typeof global2 !== "undefined" && global2 || typeof self !== "undefined" && self || function() {
    return this;
  }.call(null) || Function("return this")();
  var google_protobuf_timestamp_pb = require_timestamp_pb();
  goog.object.extend(proto, google_protobuf_timestamp_pb);
  goog.exportSymbol("proto.icemdatamonitor.v1.BootStage", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.BootStreamRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.BusZoneKind", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.EmuleState", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.EngineAction", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.EngineControlRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.HostStatus", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.HostStatusRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.LayerControlRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.LayerKind", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.LayerState", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.MetricsSample", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.NavigateUiRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.NavigateUiResponse", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.RegisterDelta", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.RegisterDelta.ValueCase", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.RegisterValue", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.RegisterValue.ValueCase", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.RegistersReply", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.RegistersRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.SetConsoleVisibleRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.SubscribeRequest", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.UiSection", null, global2);
  goog.exportSymbol("proto.icemdatamonitor.v1.ZoneHealth", null, global2);
  proto.icemdatamonitor.v1.HostStatusRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.HostStatusRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.HostStatusRequest.displayName = "proto.icemdatamonitor.v1.HostStatusRequest";
  }
  proto.icemdatamonitor.v1.HostStatus = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, proto.icemdatamonitor.v1.HostStatus.repeatedFields_, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.HostStatus, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.HostStatus.displayName = "proto.icemdatamonitor.v1.HostStatus";
  }
  proto.icemdatamonitor.v1.ZoneHealth = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.ZoneHealth, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.ZoneHealth.displayName = "proto.icemdatamonitor.v1.ZoneHealth";
  }
  proto.icemdatamonitor.v1.LayerState = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.LayerState, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.LayerState.displayName = "proto.icemdatamonitor.v1.LayerState";
  }
  proto.icemdatamonitor.v1.EmuleState = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.EmuleState, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.EmuleState.displayName = "proto.icemdatamonitor.v1.EmuleState";
  }
  proto.icemdatamonitor.v1.SubscribeRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.SubscribeRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.SubscribeRequest.displayName = "proto.icemdatamonitor.v1.SubscribeRequest";
  }
  proto.icemdatamonitor.v1.MetricsSample = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, proto.icemdatamonitor.v1.MetricsSample.repeatedFields_, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.MetricsSample, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.MetricsSample.displayName = "proto.icemdatamonitor.v1.MetricsSample";
  }
  proto.icemdatamonitor.v1.RegisterDelta = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_);
  };
  goog.inherits(proto.icemdatamonitor.v1.RegisterDelta, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.RegisterDelta.displayName = "proto.icemdatamonitor.v1.RegisterDelta";
  }
  proto.icemdatamonitor.v1.RegistersRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.RegistersRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.RegistersRequest.displayName = "proto.icemdatamonitor.v1.RegistersRequest";
  }
  proto.icemdatamonitor.v1.RegistersReply = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, proto.icemdatamonitor.v1.RegistersReply.repeatedFields_, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.RegistersReply, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.RegistersReply.displayName = "proto.icemdatamonitor.v1.RegistersReply";
  }
  proto.icemdatamonitor.v1.RegisterValue = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, proto.icemdatamonitor.v1.RegisterValue.oneofGroups_);
  };
  goog.inherits(proto.icemdatamonitor.v1.RegisterValue, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.RegisterValue.displayName = "proto.icemdatamonitor.v1.RegisterValue";
  }
  proto.icemdatamonitor.v1.LayerControlRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.LayerControlRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.LayerControlRequest.displayName = "proto.icemdatamonitor.v1.LayerControlRequest";
  }
  proto.icemdatamonitor.v1.EngineControlRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.EngineControlRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.EngineControlRequest.displayName = "proto.icemdatamonitor.v1.EngineControlRequest";
  }
  proto.icemdatamonitor.v1.BootStreamRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.BootStreamRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.BootStreamRequest.displayName = "proto.icemdatamonitor.v1.BootStreamRequest";
  }
  proto.icemdatamonitor.v1.BootStage = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.BootStage, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.BootStage.displayName = "proto.icemdatamonitor.v1.BootStage";
  }
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.SetConsoleVisibleRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.SetConsoleVisibleRequest.displayName = "proto.icemdatamonitor.v1.SetConsoleVisibleRequest";
  }
  proto.icemdatamonitor.v1.NavigateUiRequest = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.NavigateUiRequest, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.NavigateUiRequest.displayName = "proto.icemdatamonitor.v1.NavigateUiRequest";
  }
  proto.icemdatamonitor.v1.NavigateUiResponse = function(opt_data) {
    jspb.Message.initialize(this, opt_data, 0, -1, null, null);
  };
  goog.inherits(proto.icemdatamonitor.v1.NavigateUiResponse, jspb.Message);
  if (goog.DEBUG && !COMPILED) {
    proto.icemdatamonitor.v1.NavigateUiResponse.displayName = "proto.icemdatamonitor.v1.NavigateUiResponse";
  }
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.HostStatusRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.HostStatusRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.HostStatusRequest.toObject = function(includeInstance, msg) {
      var f, obj = {};
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.HostStatusRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.HostStatusRequest;
    return proto.icemdatamonitor.v1.HostStatusRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.HostStatusRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.HostStatusRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.HostStatusRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.HostStatusRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
  };
  proto.icemdatamonitor.v1.HostStatus.repeatedFields_ = [7];
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.HostStatus.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.HostStatus.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.HostStatus.toObject = function(includeInstance, msg) {
      var f, obj = {
        busOpen: jspb.Message.getBooleanFieldWithDefault(msg, 1, false),
        addinAlive: jspb.Message.getBooleanFieldWithDefault(msg, 2, false),
        heartbeat: jspb.Message.getFieldWithDefault(msg, 3, 0),
        comPort: jspb.Message.getFieldWithDefault(msg, 4, 0),
        caseState: jspb.Message.getFieldWithDefault(msg, 5, 0),
        caseLanState: jspb.Message.getFieldWithDefault(msg, 6, 0),
        zonesList: jspb.Message.toObjectList(msg.getZonesList(), proto.icemdatamonitor.v1.ZoneHealth.toObject, includeInstance),
        layers: (f = msg.getLayers()) && proto.icemdatamonitor.v1.LayerState.toObject(includeInstance, f),
        engine: (f = msg.getEngine()) && proto.icemdatamonitor.v1.EmuleState.toObject(includeInstance, f),
        updatedAtUtc: (f = msg.getUpdatedAtUtc()) && google_protobuf_timestamp_pb.Timestamp.toObject(includeInstance, f),
        consoleVisible: jspb.Message.getBooleanFieldWithDefault(msg, 11, false)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.HostStatus.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.HostStatus;
    return proto.icemdatamonitor.v1.HostStatus.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.HostStatus.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readBool();
          msg.setBusOpen(value);
          break;
        case 2:
          var value = reader.readBool();
          msg.setAddinAlive(value);
          break;
        case 3:
          var value = reader.readInt32();
          msg.setHeartbeat(value);
          break;
        case 4:
          var value = reader.readInt32();
          msg.setComPort(value);
          break;
        case 5:
          var value = reader.readInt32();
          msg.setCaseState(value);
          break;
        case 6:
          var value = reader.readInt32();
          msg.setCaseLanState(value);
          break;
        case 7:
          var value = new proto.icemdatamonitor.v1.ZoneHealth;
          reader.readMessage(value, proto.icemdatamonitor.v1.ZoneHealth.deserializeBinaryFromReader);
          msg.addZones(value);
          break;
        case 8:
          var value = new proto.icemdatamonitor.v1.LayerState;
          reader.readMessage(value, proto.icemdatamonitor.v1.LayerState.deserializeBinaryFromReader);
          msg.setLayers(value);
          break;
        case 9:
          var value = new proto.icemdatamonitor.v1.EmuleState;
          reader.readMessage(value, proto.icemdatamonitor.v1.EmuleState.deserializeBinaryFromReader);
          msg.setEngine(value);
          break;
        case 10:
          var value = new google_protobuf_timestamp_pb.Timestamp;
          reader.readMessage(value, google_protobuf_timestamp_pb.Timestamp.deserializeBinaryFromReader);
          msg.setUpdatedAtUtc(value);
          break;
        case 11:
          var value = reader.readBool();
          msg.setConsoleVisible(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.HostStatus.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.HostStatus.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getBusOpen();
    if (f) {
      writer.writeBool(1, f);
    }
    f = message.getAddinAlive();
    if (f) {
      writer.writeBool(2, f);
    }
    f = message.getHeartbeat();
    if (f !== 0) {
      writer.writeInt32(3, f);
    }
    f = message.getComPort();
    if (f !== 0) {
      writer.writeInt32(4, f);
    }
    f = message.getCaseState();
    if (f !== 0) {
      writer.writeInt32(5, f);
    }
    f = message.getCaseLanState();
    if (f !== 0) {
      writer.writeInt32(6, f);
    }
    f = message.getZonesList();
    if (f.length > 0) {
      writer.writeRepeatedMessage(7, f, proto.icemdatamonitor.v1.ZoneHealth.serializeBinaryToWriter);
    }
    f = message.getLayers();
    if (f != null) {
      writer.writeMessage(8, f, proto.icemdatamonitor.v1.LayerState.serializeBinaryToWriter);
    }
    f = message.getEngine();
    if (f != null) {
      writer.writeMessage(9, f, proto.icemdatamonitor.v1.EmuleState.serializeBinaryToWriter);
    }
    f = message.getUpdatedAtUtc();
    if (f != null) {
      writer.writeMessage(10, f, google_protobuf_timestamp_pb.Timestamp.serializeBinaryToWriter);
    }
    f = message.getConsoleVisible();
    if (f) {
      writer.writeBool(11, f);
    }
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getBusOpen = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 1, false);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setBusOpen = function(value) {
    return jspb.Message.setProto3BooleanField(this, 1, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getAddinAlive = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 2, false);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setAddinAlive = function(value) {
    return jspb.Message.setProto3BooleanField(this, 2, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getHeartbeat = function() {
    return jspb.Message.getFieldWithDefault(this, 3, 0);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setHeartbeat = function(value) {
    return jspb.Message.setProto3IntField(this, 3, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getComPort = function() {
    return jspb.Message.getFieldWithDefault(this, 4, 0);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setComPort = function(value) {
    return jspb.Message.setProto3IntField(this, 4, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getCaseState = function() {
    return jspb.Message.getFieldWithDefault(this, 5, 0);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setCaseState = function(value) {
    return jspb.Message.setProto3IntField(this, 5, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getCaseLanState = function() {
    return jspb.Message.getFieldWithDefault(this, 6, 0);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setCaseLanState = function(value) {
    return jspb.Message.setProto3IntField(this, 6, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getZonesList = function() {
    return jspb.Message.getRepeatedWrapperField(this, proto.icemdatamonitor.v1.ZoneHealth, 7);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setZonesList = function(value) {
    return jspb.Message.setRepeatedWrapperField(this, 7, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.addZones = function(opt_value, opt_index) {
    return jspb.Message.addToRepeatedWrapperField(this, 7, opt_value, proto.icemdatamonitor.v1.ZoneHealth, opt_index);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.clearZonesList = function() {
    return this.setZonesList([]);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getLayers = function() {
    return jspb.Message.getWrapperField(this, proto.icemdatamonitor.v1.LayerState, 8);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setLayers = function(value) {
    return jspb.Message.setWrapperField(this, 8, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.clearLayers = function() {
    return this.setLayers(undefined);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.hasLayers = function() {
    return jspb.Message.getField(this, 8) != null;
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getEngine = function() {
    return jspb.Message.getWrapperField(this, proto.icemdatamonitor.v1.EmuleState, 9);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setEngine = function(value) {
    return jspb.Message.setWrapperField(this, 9, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.clearEngine = function() {
    return this.setEngine(undefined);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.hasEngine = function() {
    return jspb.Message.getField(this, 9) != null;
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getUpdatedAtUtc = function() {
    return jspb.Message.getWrapperField(this, google_protobuf_timestamp_pb.Timestamp, 10);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setUpdatedAtUtc = function(value) {
    return jspb.Message.setWrapperField(this, 10, value);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.clearUpdatedAtUtc = function() {
    return this.setUpdatedAtUtc(undefined);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.hasUpdatedAtUtc = function() {
    return jspb.Message.getField(this, 10) != null;
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.getConsoleVisible = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 11, false);
  };
  proto.icemdatamonitor.v1.HostStatus.prototype.setConsoleVisible = function(value) {
    return jspb.Message.setProto3BooleanField(this, 11, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.ZoneHealth.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.ZoneHealth.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.ZoneHealth.toObject = function(includeInstance, msg) {
      var f, obj = {
        kind: jspb.Message.getFieldWithDefault(msg, 1, 0),
        active: jspb.Message.getBooleanFieldWithDefault(msg, 2, false),
        ageMs: jspb.Message.getFieldWithDefault(msg, 3, 0),
        writeCount: jspb.Message.getFieldWithDefault(msg, 4, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.ZoneHealth.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.ZoneHealth;
    return proto.icemdatamonitor.v1.ZoneHealth.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.ZoneHealth.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readEnum();
          msg.setKind(value);
          break;
        case 2:
          var value = reader.readBool();
          msg.setActive(value);
          break;
        case 3:
          var value = reader.readInt64();
          msg.setAgeMs(value);
          break;
        case 4:
          var value = reader.readUint32();
          msg.setWriteCount(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.ZoneHealth.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.ZoneHealth.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getKind();
    if (f !== 0) {
      writer.writeEnum(1, f);
    }
    f = message.getActive();
    if (f) {
      writer.writeBool(2, f);
    }
    f = message.getAgeMs();
    if (f !== 0) {
      writer.writeInt64(3, f);
    }
    f = message.getWriteCount();
    if (f !== 0) {
      writer.writeUint32(4, f);
    }
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.getKind = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.setKind = function(value) {
    return jspb.Message.setProto3EnumField(this, 1, value);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.getActive = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 2, false);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.setActive = function(value) {
    return jspb.Message.setProto3BooleanField(this, 2, value);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.getAgeMs = function() {
    return jspb.Message.getFieldWithDefault(this, 3, 0);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.setAgeMs = function(value) {
    return jspb.Message.setProto3IntField(this, 3, value);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.getWriteCount = function() {
    return jspb.Message.getFieldWithDefault(this, 4, 0);
  };
  proto.icemdatamonitor.v1.ZoneHealth.prototype.setWriteCount = function(value) {
    return jspb.Message.setProto3IntField(this, 4, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.LayerState.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.LayerState.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.LayerState.toObject = function(includeInstance, msg) {
      var f, obj = {
        modbus: jspb.Message.getBooleanFieldWithDefault(msg, 1, false),
        modbusBound: jspb.Message.getBooleanFieldWithDefault(msg, 2, false),
        modbusPort: jspb.Message.getFieldWithDefault(msg, 3, 0),
        web: jspb.Message.getBooleanFieldWithDefault(msg, 4, false),
        sniffer: jspb.Message.getBooleanFieldWithDefault(msg, 5, false),
        snifferAvailable: jspb.Message.getBooleanFieldWithDefault(msg, 6, false),
        opcDa: jspb.Message.getBooleanFieldWithDefault(msg, 7, false),
        setAsDefault: jspb.Message.getBooleanFieldWithDefault(msg, 8, false),
        busy: jspb.Message.getBooleanFieldWithDefault(msg, 9, false),
        lastError: jspb.Message.getFieldWithDefault(msg, 10, "")
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.LayerState.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.LayerState;
    return proto.icemdatamonitor.v1.LayerState.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.LayerState.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readBool();
          msg.setModbus(value);
          break;
        case 2:
          var value = reader.readBool();
          msg.setModbusBound(value);
          break;
        case 3:
          var value = reader.readInt32();
          msg.setModbusPort(value);
          break;
        case 4:
          var value = reader.readBool();
          msg.setWeb(value);
          break;
        case 5:
          var value = reader.readBool();
          msg.setSniffer(value);
          break;
        case 6:
          var value = reader.readBool();
          msg.setSnifferAvailable(value);
          break;
        case 7:
          var value = reader.readBool();
          msg.setOpcDa(value);
          break;
        case 8:
          var value = reader.readBool();
          msg.setSetAsDefault(value);
          break;
        case 9:
          var value = reader.readBool();
          msg.setBusy(value);
          break;
        case 10:
          var value = reader.readString();
          msg.setLastError(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.LayerState.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.LayerState.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.LayerState.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getModbus();
    if (f) {
      writer.writeBool(1, f);
    }
    f = message.getModbusBound();
    if (f) {
      writer.writeBool(2, f);
    }
    f = message.getModbusPort();
    if (f !== 0) {
      writer.writeInt32(3, f);
    }
    f = message.getWeb();
    if (f) {
      writer.writeBool(4, f);
    }
    f = message.getSniffer();
    if (f) {
      writer.writeBool(5, f);
    }
    f = message.getSnifferAvailable();
    if (f) {
      writer.writeBool(6, f);
    }
    f = message.getOpcDa();
    if (f) {
      writer.writeBool(7, f);
    }
    f = message.getSetAsDefault();
    if (f) {
      writer.writeBool(8, f);
    }
    f = message.getBusy();
    if (f) {
      writer.writeBool(9, f);
    }
    f = message.getLastError();
    if (f.length > 0) {
      writer.writeString(10, f);
    }
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getModbus = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 1, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setModbus = function(value) {
    return jspb.Message.setProto3BooleanField(this, 1, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getModbusBound = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 2, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setModbusBound = function(value) {
    return jspb.Message.setProto3BooleanField(this, 2, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getModbusPort = function() {
    return jspb.Message.getFieldWithDefault(this, 3, 0);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setModbusPort = function(value) {
    return jspb.Message.setProto3IntField(this, 3, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getWeb = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 4, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setWeb = function(value) {
    return jspb.Message.setProto3BooleanField(this, 4, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getSniffer = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 5, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setSniffer = function(value) {
    return jspb.Message.setProto3BooleanField(this, 5, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getSnifferAvailable = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 6, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setSnifferAvailable = function(value) {
    return jspb.Message.setProto3BooleanField(this, 6, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getOpcDa = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 7, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setOpcDa = function(value) {
    return jspb.Message.setProto3BooleanField(this, 7, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getSetAsDefault = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 8, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setSetAsDefault = function(value) {
    return jspb.Message.setProto3BooleanField(this, 8, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getBusy = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 9, false);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setBusy = function(value) {
    return jspb.Message.setProto3BooleanField(this, 9, value);
  };
  proto.icemdatamonitor.v1.LayerState.prototype.getLastError = function() {
    return jspb.Message.getFieldWithDefault(this, 10, "");
  };
  proto.icemdatamonitor.v1.LayerState.prototype.setLastError = function(value) {
    return jspb.Message.setProto3StringField(this, 10, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.EmuleState.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.EmuleState.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.EmuleState.toObject = function(includeInstance, msg) {
      var f, obj = {
        enabled: jspb.Message.getBooleanFieldWithDefault(msg, 1, false),
        pid: jspb.Message.getFieldWithDefault(msg, 2, 0),
        running: jspb.Message.getBooleanFieldWithDefault(msg, 3, false)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.EmuleState.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.EmuleState;
    return proto.icemdatamonitor.v1.EmuleState.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.EmuleState.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readBool();
          msg.setEnabled(value);
          break;
        case 2:
          var value = reader.readInt32();
          msg.setPid(value);
          break;
        case 3:
          var value = reader.readBool();
          msg.setRunning(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.EmuleState.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.EmuleState.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getEnabled();
    if (f) {
      writer.writeBool(1, f);
    }
    f = message.getPid();
    if (f !== 0) {
      writer.writeInt32(2, f);
    }
    f = message.getRunning();
    if (f) {
      writer.writeBool(3, f);
    }
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.getEnabled = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 1, false);
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.setEnabled = function(value) {
    return jspb.Message.setProto3BooleanField(this, 1, value);
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.getPid = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.setPid = function(value) {
    return jspb.Message.setProto3IntField(this, 2, value);
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.getRunning = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 3, false);
  };
  proto.icemdatamonitor.v1.EmuleState.prototype.setRunning = function(value) {
    return jspb.Message.setProto3BooleanField(this, 3, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.SubscribeRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.SubscribeRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.SubscribeRequest.toObject = function(includeInstance, msg) {
      var f, obj = {
        intervalMs: jspb.Message.getFieldWithDefault(msg, 1, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.SubscribeRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.SubscribeRequest;
    return proto.icemdatamonitor.v1.SubscribeRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.SubscribeRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readInt32();
          msg.setIntervalMs(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.SubscribeRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.SubscribeRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.SubscribeRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getIntervalMs();
    if (f !== 0) {
      writer.writeInt32(1, f);
    }
  };
  proto.icemdatamonitor.v1.SubscribeRequest.prototype.getIntervalMs = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.SubscribeRequest.prototype.setIntervalMs = function(value) {
    return jspb.Message.setProto3IntField(this, 1, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.repeatedFields_ = [6];
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.MetricsSample.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.MetricsSample.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.MetricsSample.toObject = function(includeInstance, msg) {
      var f, obj = {
        timestampUtc: (f = msg.getTimestampUtc()) && google_protobuf_timestamp_pb.Timestamp.toObject(includeInstance, f),
        heartbeat: jspb.Message.getFieldWithDefault(msg, 2, 0),
        comPortActive: jspb.Message.getBooleanFieldWithDefault(msg, 3, false),
        caseState: jspb.Message.getFieldWithDefault(msg, 4, 0),
        intervalClamped: jspb.Message.getBooleanFieldWithDefault(msg, 5, false),
        deltasList: jspb.Message.toObjectList(msg.getDeltasList(), proto.icemdatamonitor.v1.RegisterDelta.toObject, includeInstance)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.MetricsSample.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.MetricsSample;
    return proto.icemdatamonitor.v1.MetricsSample.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.MetricsSample.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = new google_protobuf_timestamp_pb.Timestamp;
          reader.readMessage(value, google_protobuf_timestamp_pb.Timestamp.deserializeBinaryFromReader);
          msg.setTimestampUtc(value);
          break;
        case 2:
          var value = reader.readInt32();
          msg.setHeartbeat(value);
          break;
        case 3:
          var value = reader.readBool();
          msg.setComPortActive(value);
          break;
        case 4:
          var value = reader.readInt32();
          msg.setCaseState(value);
          break;
        case 5:
          var value = reader.readBool();
          msg.setIntervalClamped(value);
          break;
        case 6:
          var value = new proto.icemdatamonitor.v1.RegisterDelta;
          reader.readMessage(value, proto.icemdatamonitor.v1.RegisterDelta.deserializeBinaryFromReader);
          msg.addDeltas(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.MetricsSample.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.MetricsSample.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getTimestampUtc();
    if (f != null) {
      writer.writeMessage(1, f, google_protobuf_timestamp_pb.Timestamp.serializeBinaryToWriter);
    }
    f = message.getHeartbeat();
    if (f !== 0) {
      writer.writeInt32(2, f);
    }
    f = message.getComPortActive();
    if (f) {
      writer.writeBool(3, f);
    }
    f = message.getCaseState();
    if (f !== 0) {
      writer.writeInt32(4, f);
    }
    f = message.getIntervalClamped();
    if (f) {
      writer.writeBool(5, f);
    }
    f = message.getDeltasList();
    if (f.length > 0) {
      writer.writeRepeatedMessage(6, f, proto.icemdatamonitor.v1.RegisterDelta.serializeBinaryToWriter);
    }
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.getTimestampUtc = function() {
    return jspb.Message.getWrapperField(this, google_protobuf_timestamp_pb.Timestamp, 1);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.setTimestampUtc = function(value) {
    return jspb.Message.setWrapperField(this, 1, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.clearTimestampUtc = function() {
    return this.setTimestampUtc(undefined);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.hasTimestampUtc = function() {
    return jspb.Message.getField(this, 1) != null;
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.getHeartbeat = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.setHeartbeat = function(value) {
    return jspb.Message.setProto3IntField(this, 2, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.getComPortActive = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 3, false);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.setComPortActive = function(value) {
    return jspb.Message.setProto3BooleanField(this, 3, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.getCaseState = function() {
    return jspb.Message.getFieldWithDefault(this, 4, 0);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.setCaseState = function(value) {
    return jspb.Message.setProto3IntField(this, 4, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.getIntervalClamped = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 5, false);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.setIntervalClamped = function(value) {
    return jspb.Message.setProto3BooleanField(this, 5, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.getDeltasList = function() {
    return jspb.Message.getRepeatedWrapperField(this, proto.icemdatamonitor.v1.RegisterDelta, 6);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.setDeltasList = function(value) {
    return jspb.Message.setRepeatedWrapperField(this, 6, value);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.addDeltas = function(opt_value, opt_index) {
    return jspb.Message.addToRepeatedWrapperField(this, 6, opt_value, proto.icemdatamonitor.v1.RegisterDelta, opt_index);
  };
  proto.icemdatamonitor.v1.MetricsSample.prototype.clearDeltasList = function() {
    return this.setDeltasList([]);
  };
  proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_ = [[2, 3]];
  proto.icemdatamonitor.v1.RegisterDelta.ValueCase = {
    VALUE_NOT_SET: 0,
    INT32_VALUE: 2,
    FLOAT_VALUE: 3
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.getValueCase = function() {
    return jspb.Message.computeOneofCase(this, proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_[0]);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.RegisterDelta.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.RegisterDelta.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.RegisterDelta.toObject = function(includeInstance, msg) {
      var f, obj = {
        addr: jspb.Message.getFieldWithDefault(msg, 1, 0),
        int32Value: (f = jspb.Message.getField(msg, 2)) == null ? undefined : f,
        floatValue: (f = jspb.Message.getOptionalFloatingPointField(msg, 3)) == null ? undefined : f
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.RegisterDelta.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.RegisterDelta;
    return proto.icemdatamonitor.v1.RegisterDelta.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.RegisterDelta.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readUint32();
          msg.setAddr(value);
          break;
        case 2:
          var value = reader.readInt32();
          msg.setInt32Value(value);
          break;
        case 3:
          var value = reader.readFloat();
          msg.setFloatValue(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.RegisterDelta.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.RegisterDelta.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getAddr();
    if (f !== 0) {
      writer.writeUint32(1, f);
    }
    f = jspb.Message.getField(message, 2);
    if (f != null) {
      writer.writeInt32(2, f);
    }
    f = jspb.Message.getField(message, 3);
    if (f != null) {
      writer.writeFloat(3, f);
    }
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.getAddr = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.setAddr = function(value) {
    return jspb.Message.setProto3IntField(this, 1, value);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.getInt32Value = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.setInt32Value = function(value) {
    return jspb.Message.setOneofField(this, 2, proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_[0], value);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.clearInt32Value = function() {
    return jspb.Message.setOneofField(this, 2, proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_[0], undefined);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.hasInt32Value = function() {
    return jspb.Message.getField(this, 2) != null;
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.getFloatValue = function() {
    return jspb.Message.getFloatingPointFieldWithDefault(this, 3, 0);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.setFloatValue = function(value) {
    return jspb.Message.setOneofField(this, 3, proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_[0], value);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.clearFloatValue = function() {
    return jspb.Message.setOneofField(this, 3, proto.icemdatamonitor.v1.RegisterDelta.oneofGroups_[0], undefined);
  };
  proto.icemdatamonitor.v1.RegisterDelta.prototype.hasFloatValue = function() {
    return jspb.Message.getField(this, 3) != null;
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.RegistersRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.RegistersRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.RegistersRequest.toObject = function(includeInstance, msg) {
      var f, obj = {
        zone: jspb.Message.getFieldWithDefault(msg, 1, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.RegistersRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.RegistersRequest;
    return proto.icemdatamonitor.v1.RegistersRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.RegistersRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readEnum();
          msg.setZone(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.RegistersRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.RegistersRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.RegistersRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getZone();
    if (f !== 0) {
      writer.writeEnum(1, f);
    }
  };
  proto.icemdatamonitor.v1.RegistersRequest.prototype.getZone = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.RegistersRequest.prototype.setZone = function(value) {
    return jspb.Message.setProto3EnumField(this, 1, value);
  };
  proto.icemdatamonitor.v1.RegistersReply.repeatedFields_ = [3];
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.RegistersReply.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.RegistersReply.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.RegistersReply.toObject = function(includeInstance, msg) {
      var f, obj = {
        timestampUtc: (f = msg.getTimestampUtc()) && google_protobuf_timestamp_pb.Timestamp.toObject(includeInstance, f),
        heartbeat: jspb.Message.getFieldWithDefault(msg, 2, 0),
        valuesList: jspb.Message.toObjectList(msg.getValuesList(), proto.icemdatamonitor.v1.RegisterValue.toObject, includeInstance)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.RegistersReply.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.RegistersReply;
    return proto.icemdatamonitor.v1.RegistersReply.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.RegistersReply.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = new google_protobuf_timestamp_pb.Timestamp;
          reader.readMessage(value, google_protobuf_timestamp_pb.Timestamp.deserializeBinaryFromReader);
          msg.setTimestampUtc(value);
          break;
        case 2:
          var value = reader.readInt32();
          msg.setHeartbeat(value);
          break;
        case 3:
          var value = new proto.icemdatamonitor.v1.RegisterValue;
          reader.readMessage(value, proto.icemdatamonitor.v1.RegisterValue.deserializeBinaryFromReader);
          msg.addValues(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.RegistersReply.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.RegistersReply.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getTimestampUtc();
    if (f != null) {
      writer.writeMessage(1, f, google_protobuf_timestamp_pb.Timestamp.serializeBinaryToWriter);
    }
    f = message.getHeartbeat();
    if (f !== 0) {
      writer.writeInt32(2, f);
    }
    f = message.getValuesList();
    if (f.length > 0) {
      writer.writeRepeatedMessage(3, f, proto.icemdatamonitor.v1.RegisterValue.serializeBinaryToWriter);
    }
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.getTimestampUtc = function() {
    return jspb.Message.getWrapperField(this, google_protobuf_timestamp_pb.Timestamp, 1);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.setTimestampUtc = function(value) {
    return jspb.Message.setWrapperField(this, 1, value);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.clearTimestampUtc = function() {
    return this.setTimestampUtc(undefined);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.hasTimestampUtc = function() {
    return jspb.Message.getField(this, 1) != null;
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.getHeartbeat = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.setHeartbeat = function(value) {
    return jspb.Message.setProto3IntField(this, 2, value);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.getValuesList = function() {
    return jspb.Message.getRepeatedWrapperField(this, proto.icemdatamonitor.v1.RegisterValue, 3);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.setValuesList = function(value) {
    return jspb.Message.setRepeatedWrapperField(this, 3, value);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.addValues = function(opt_value, opt_index) {
    return jspb.Message.addToRepeatedWrapperField(this, 3, opt_value, proto.icemdatamonitor.v1.RegisterValue, opt_index);
  };
  proto.icemdatamonitor.v1.RegistersReply.prototype.clearValuesList = function() {
    return this.setValuesList([]);
  };
  proto.icemdatamonitor.v1.RegisterValue.oneofGroups_ = [[2, 3]];
  proto.icemdatamonitor.v1.RegisterValue.ValueCase = {
    VALUE_NOT_SET: 0,
    INT32_VALUE: 2,
    FLOAT_VALUE: 3
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.getValueCase = function() {
    return jspb.Message.computeOneofCase(this, proto.icemdatamonitor.v1.RegisterValue.oneofGroups_[0]);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.RegisterValue.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.RegisterValue.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.RegisterValue.toObject = function(includeInstance, msg) {
      var f, obj = {
        addr: jspb.Message.getFieldWithDefault(msg, 1, 0),
        int32Value: (f = jspb.Message.getField(msg, 2)) == null ? undefined : f,
        floatValue: (f = jspb.Message.getOptionalFloatingPointField(msg, 3)) == null ? undefined : f
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.RegisterValue.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.RegisterValue;
    return proto.icemdatamonitor.v1.RegisterValue.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.RegisterValue.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readUint32();
          msg.setAddr(value);
          break;
        case 2:
          var value = reader.readInt32();
          msg.setInt32Value(value);
          break;
        case 3:
          var value = reader.readFloat();
          msg.setFloatValue(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.RegisterValue.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.RegisterValue.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getAddr();
    if (f !== 0) {
      writer.writeUint32(1, f);
    }
    f = jspb.Message.getField(message, 2);
    if (f != null) {
      writer.writeInt32(2, f);
    }
    f = jspb.Message.getField(message, 3);
    if (f != null) {
      writer.writeFloat(3, f);
    }
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.getAddr = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.setAddr = function(value) {
    return jspb.Message.setProto3IntField(this, 1, value);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.getInt32Value = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.setInt32Value = function(value) {
    return jspb.Message.setOneofField(this, 2, proto.icemdatamonitor.v1.RegisterValue.oneofGroups_[0], value);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.clearInt32Value = function() {
    return jspb.Message.setOneofField(this, 2, proto.icemdatamonitor.v1.RegisterValue.oneofGroups_[0], undefined);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.hasInt32Value = function() {
    return jspb.Message.getField(this, 2) != null;
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.getFloatValue = function() {
    return jspb.Message.getFloatingPointFieldWithDefault(this, 3, 0);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.setFloatValue = function(value) {
    return jspb.Message.setOneofField(this, 3, proto.icemdatamonitor.v1.RegisterValue.oneofGroups_[0], value);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.clearFloatValue = function() {
    return jspb.Message.setOneofField(this, 3, proto.icemdatamonitor.v1.RegisterValue.oneofGroups_[0], undefined);
  };
  proto.icemdatamonitor.v1.RegisterValue.prototype.hasFloatValue = function() {
    return jspb.Message.getField(this, 3) != null;
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.LayerControlRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.LayerControlRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.LayerControlRequest.toObject = function(includeInstance, msg) {
      var f, obj = {
        layer: jspb.Message.getFieldWithDefault(msg, 1, 0),
        desiredState: jspb.Message.getBooleanFieldWithDefault(msg, 2, false)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.LayerControlRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.LayerControlRequest;
    return proto.icemdatamonitor.v1.LayerControlRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.LayerControlRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readEnum();
          msg.setLayer(value);
          break;
        case 2:
          var value = reader.readBool();
          msg.setDesiredState(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.LayerControlRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.LayerControlRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.LayerControlRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getLayer();
    if (f !== 0) {
      writer.writeEnum(1, f);
    }
    f = message.getDesiredState();
    if (f) {
      writer.writeBool(2, f);
    }
  };
  proto.icemdatamonitor.v1.LayerControlRequest.prototype.getLayer = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.LayerControlRequest.prototype.setLayer = function(value) {
    return jspb.Message.setProto3EnumField(this, 1, value);
  };
  proto.icemdatamonitor.v1.LayerControlRequest.prototype.getDesiredState = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 2, false);
  };
  proto.icemdatamonitor.v1.LayerControlRequest.prototype.setDesiredState = function(value) {
    return jspb.Message.setProto3BooleanField(this, 2, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.EngineControlRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.EngineControlRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.EngineControlRequest.toObject = function(includeInstance, msg) {
      var f, obj = {
        action: jspb.Message.getFieldWithDefault(msg, 1, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.EngineControlRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.EngineControlRequest;
    return proto.icemdatamonitor.v1.EngineControlRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.EngineControlRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readEnum();
          msg.setAction(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.EngineControlRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.EngineControlRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.EngineControlRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getAction();
    if (f !== 0) {
      writer.writeEnum(1, f);
    }
  };
  proto.icemdatamonitor.v1.EngineControlRequest.prototype.getAction = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.EngineControlRequest.prototype.setAction = function(value) {
    return jspb.Message.setProto3EnumField(this, 1, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.BootStreamRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.BootStreamRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.BootStreamRequest.toObject = function(includeInstance, msg) {
      var f, obj = {};
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.BootStreamRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.BootStreamRequest;
    return proto.icemdatamonitor.v1.BootStreamRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.BootStreamRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.BootStreamRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.BootStreamRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.BootStreamRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.BootStage.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.BootStage.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.BootStage.toObject = function(includeInstance, msg) {
      var f, obj = {
        ordinal: jspb.Message.getFieldWithDefault(msg, 1, 0),
        id: jspb.Message.getFieldWithDefault(msg, 2, ""),
        title: jspb.Message.getFieldWithDefault(msg, 3, ""),
        completed: jspb.Message.getBooleanFieldWithDefault(msg, 4, false),
        detail: jspb.Message.getFieldWithDefault(msg, 5, "")
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.BootStage.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.BootStage;
    return proto.icemdatamonitor.v1.BootStage.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.BootStage.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readUint32();
          msg.setOrdinal(value);
          break;
        case 2:
          var value = reader.readString();
          msg.setId(value);
          break;
        case 3:
          var value = reader.readString();
          msg.setTitle(value);
          break;
        case 4:
          var value = reader.readBool();
          msg.setCompleted(value);
          break;
        case 5:
          var value = reader.readString();
          msg.setDetail(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.BootStage.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.BootStage.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.BootStage.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getOrdinal();
    if (f !== 0) {
      writer.writeUint32(1, f);
    }
    f = message.getId();
    if (f.length > 0) {
      writer.writeString(2, f);
    }
    f = message.getTitle();
    if (f.length > 0) {
      writer.writeString(3, f);
    }
    f = message.getCompleted();
    if (f) {
      writer.writeBool(4, f);
    }
    f = message.getDetail();
    if (f.length > 0) {
      writer.writeString(5, f);
    }
  };
  proto.icemdatamonitor.v1.BootStage.prototype.getOrdinal = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.BootStage.prototype.setOrdinal = function(value) {
    return jspb.Message.setProto3IntField(this, 1, value);
  };
  proto.icemdatamonitor.v1.BootStage.prototype.getId = function() {
    return jspb.Message.getFieldWithDefault(this, 2, "");
  };
  proto.icemdatamonitor.v1.BootStage.prototype.setId = function(value) {
    return jspb.Message.setProto3StringField(this, 2, value);
  };
  proto.icemdatamonitor.v1.BootStage.prototype.getTitle = function() {
    return jspb.Message.getFieldWithDefault(this, 3, "");
  };
  proto.icemdatamonitor.v1.BootStage.prototype.setTitle = function(value) {
    return jspb.Message.setProto3StringField(this, 3, value);
  };
  proto.icemdatamonitor.v1.BootStage.prototype.getCompleted = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 4, false);
  };
  proto.icemdatamonitor.v1.BootStage.prototype.setCompleted = function(value) {
    return jspb.Message.setProto3BooleanField(this, 4, value);
  };
  proto.icemdatamonitor.v1.BootStage.prototype.getDetail = function() {
    return jspb.Message.getFieldWithDefault(this, 5, "");
  };
  proto.icemdatamonitor.v1.BootStage.prototype.setDetail = function(value) {
    return jspb.Message.setProto3StringField(this, 5, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.SetConsoleVisibleRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.SetConsoleVisibleRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.SetConsoleVisibleRequest.toObject = function(includeInstance, msg) {
      var f, obj = {
        visible: jspb.Message.getBooleanFieldWithDefault(msg, 1, false)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.SetConsoleVisibleRequest;
    return proto.icemdatamonitor.v1.SetConsoleVisibleRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readBool();
          msg.setVisible(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.SetConsoleVisibleRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getVisible();
    if (f) {
      writer.writeBool(1, f);
    }
  };
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest.prototype.getVisible = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 1, false);
  };
  proto.icemdatamonitor.v1.SetConsoleVisibleRequest.prototype.setVisible = function(value) {
    return jspb.Message.setProto3BooleanField(this, 1, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.NavigateUiRequest.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.NavigateUiRequest.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.NavigateUiRequest.toObject = function(includeInstance, msg) {
      var f, obj = {
        section: jspb.Message.getFieldWithDefault(msg, 1, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.NavigateUiRequest.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.NavigateUiRequest;
    return proto.icemdatamonitor.v1.NavigateUiRequest.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.NavigateUiRequest.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readEnum();
          msg.setSection(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.NavigateUiRequest.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.NavigateUiRequest.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.NavigateUiRequest.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getSection();
    if (f !== 0) {
      writer.writeEnum(1, f);
    }
  };
  proto.icemdatamonitor.v1.NavigateUiRequest.prototype.getSection = function() {
    return jspb.Message.getFieldWithDefault(this, 1, 0);
  };
  proto.icemdatamonitor.v1.NavigateUiRequest.prototype.setSection = function(value) {
    return jspb.Message.setProto3EnumField(this, 1, value);
  };
  if (jspb.Message.GENERATE_TO_OBJECT) {
    proto.icemdatamonitor.v1.NavigateUiResponse.prototype.toObject = function(opt_includeInstance) {
      return proto.icemdatamonitor.v1.NavigateUiResponse.toObject(opt_includeInstance, this);
    };
    proto.icemdatamonitor.v1.NavigateUiResponse.toObject = function(includeInstance, msg) {
      var f, obj = {
        uiAlive: jspb.Message.getBooleanFieldWithDefault(msg, 1, false),
        pending: jspb.Message.getFieldWithDefault(msg, 2, 0)
      };
      if (includeInstance) {
        obj.$jspbMessageInstance = msg;
      }
      return obj;
    };
  }
  proto.icemdatamonitor.v1.NavigateUiResponse.deserializeBinary = function(bytes) {
    var reader = new jspb.BinaryReader(bytes);
    var msg = new proto.icemdatamonitor.v1.NavigateUiResponse;
    return proto.icemdatamonitor.v1.NavigateUiResponse.deserializeBinaryFromReader(msg, reader);
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.deserializeBinaryFromReader = function(msg, reader) {
    while (reader.nextField()) {
      if (reader.isEndGroup()) {
        break;
      }
      var field = reader.getFieldNumber();
      switch (field) {
        case 1:
          var value = reader.readBool();
          msg.setUiAlive(value);
          break;
        case 2:
          var value = reader.readEnum();
          msg.setPending(value);
          break;
        default:
          reader.skipField();
          break;
      }
    }
    return msg;
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.prototype.serializeBinary = function() {
    var writer = new jspb.BinaryWriter;
    proto.icemdatamonitor.v1.NavigateUiResponse.serializeBinaryToWriter(this, writer);
    return writer.getResultBuffer();
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.serializeBinaryToWriter = function(message, writer) {
    var f = undefined;
    f = message.getUiAlive();
    if (f) {
      writer.writeBool(1, f);
    }
    f = message.getPending();
    if (f !== 0) {
      writer.writeEnum(2, f);
    }
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.prototype.getUiAlive = function() {
    return jspb.Message.getBooleanFieldWithDefault(this, 1, false);
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.prototype.setUiAlive = function(value) {
    return jspb.Message.setProto3BooleanField(this, 1, value);
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.prototype.getPending = function() {
    return jspb.Message.getFieldWithDefault(this, 2, 0);
  };
  proto.icemdatamonitor.v1.NavigateUiResponse.prototype.setPending = function(value) {
    return jspb.Message.setProto3EnumField(this, 2, value);
  };
  proto.icemdatamonitor.v1.LayerKind = {
    LAYER_KIND_UNSPECIFIED: 0,
    LAYER_KIND_MODBUS: 1,
    LAYER_KIND_WEB: 2,
    LAYER_KIND_LAN_SNIFFER: 3,
    LAYER_KIND_OPC_DA: 4
  };
  proto.icemdatamonitor.v1.BusZoneKind = {
    BUS_ZONE_UNSPECIFIED: 0,
    BUS_ZONE_COM: 1,
    BUS_ZONE_LAN: 2,
    BUS_ZONE_RAW: 3
  };
  proto.icemdatamonitor.v1.EngineAction = {
    ENGINE_ACTION_UNSPECIFIED: 0,
    ENGINE_ACTION_START: 1,
    ENGINE_ACTION_STOP: 2,
    ENGINE_ACTION_RESTART: 3
  };
  proto.icemdatamonitor.v1.UiSection = {
    UI_SECTION_UNSPECIFIED: 0,
    UI_SECTION_WEB: 1,
    UI_SECTION_STATUS: 2,
    UI_SECTION_EMULATOR: 3
  };
  goog.object.extend(exports, proto.icemdatamonitor.v1);
});

// src/api.ts
var grpcWeb2 = __toESM(require_grpc_web(), 1);

// src/generated/TelemetryServiceClientPb.ts
var grpcWeb = __toESM(require_grpc_web(), 1);
var telemetry_pb = __toESM(require_telemetry_pb(), 1);

class TelemetryServiceClient {
  client_;
  hostname_;
  credentials_;
  options_;
  constructor(hostname, credentials, options) {
    if (!options)
      options = {};
    if (!credentials)
      credentials = {};
    options["format"] = "binary";
    this.client_ = new grpcWeb.GrpcWebClientBase(options);
    this.hostname_ = hostname.replace(/\/+$/, "");
    this.credentials_ = credentials;
    this.options_ = options;
  }
  methodDescriptorGetStatus = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/GetStatus", grpcWeb.MethodType.UNARY, telemetry_pb.HostStatusRequest, telemetry_pb.HostStatus, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.HostStatus.deserializeBinary);
  getStatus(request, metadata, callback) {
    if (callback !== undefined) {
      return this.client_.rpcCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/GetStatus", request, metadata || {}, this.methodDescriptorGetStatus, callback);
    }
    return this.client_.unaryCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/GetStatus", request, metadata || {}, this.methodDescriptorGetStatus);
  }
  methodDescriptorSubscribeMetrics = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/SubscribeMetrics", grpcWeb.MethodType.SERVER_STREAMING, telemetry_pb.SubscribeRequest, telemetry_pb.MetricsSample, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.MetricsSample.deserializeBinary);
  subscribeMetrics(request, metadata) {
    return this.client_.serverStreaming(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/SubscribeMetrics", request, metadata || {}, this.methodDescriptorSubscribeMetrics);
  }
  methodDescriptorSetLayer = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/SetLayer", grpcWeb.MethodType.UNARY, telemetry_pb.LayerControlRequest, telemetry_pb.LayerState, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.LayerState.deserializeBinary);
  setLayer(request, metadata, callback) {
    if (callback !== undefined) {
      return this.client_.rpcCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/SetLayer", request, metadata || {}, this.methodDescriptorSetLayer, callback);
    }
    return this.client_.unaryCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/SetLayer", request, metadata || {}, this.methodDescriptorSetLayer);
  }
  // TOMBSTONE (2026-08-27 owner decree): the ControlEngine client method was
  // REMOVED — the emulator is a separate independent app; the dashboard has
  // NO sim control (the «Движок» card + fatal banner are gone from index.html).
  methodDescriptorGetRegisters = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/GetRegisters", grpcWeb.MethodType.UNARY, telemetry_pb.RegistersRequest, telemetry_pb.RegistersReply, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.RegistersReply.deserializeBinary);
  getRegisters(request, metadata, callback) {
    if (callback !== undefined) {
      return this.client_.rpcCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/GetRegisters", request, metadata || {}, this.methodDescriptorGetRegisters, callback);
    }
    return this.client_.unaryCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/GetRegisters", request, metadata || {}, this.methodDescriptorGetRegisters);
  }
  methodDescriptorSubscribeBootStages = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/SubscribeBootStages", grpcWeb.MethodType.SERVER_STREAMING, telemetry_pb.BootStreamRequest, telemetry_pb.BootStage, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.BootStage.deserializeBinary);
  subscribeBootStages(request, metadata) {
    return this.client_.serverStreaming(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/SubscribeBootStages", request, metadata || {}, this.methodDescriptorSubscribeBootStages);
  }
  methodDescriptorSetConsoleVisible = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/SetConsoleVisible", grpcWeb.MethodType.UNARY, telemetry_pb.SetConsoleVisibleRequest, telemetry_pb.HostStatus, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.HostStatus.deserializeBinary);
  setConsoleVisible(request, metadata, callback) {
    if (callback !== undefined) {
      return this.client_.rpcCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/SetConsoleVisible", request, metadata || {}, this.methodDescriptorSetConsoleVisible, callback);
    }
    return this.client_.unaryCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/SetConsoleVisible", request, metadata || {}, this.methodDescriptorSetConsoleVisible);
  }
  methodDescriptorNavigateUi = new grpcWeb.MethodDescriptor("/icemdatamonitor.v1.TelemetryService/NavigateUi", grpcWeb.MethodType.UNARY, telemetry_pb.NavigateUiRequest, telemetry_pb.NavigateUiResponse, (request) => {
    return request.serializeBinary();
  }, telemetry_pb.NavigateUiResponse.deserializeBinary);
  navigateUi(request, metadata, callback) {
    if (callback !== undefined) {
      return this.client_.rpcCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/NavigateUi", request, metadata || {}, this.methodDescriptorNavigateUi, callback);
    }
    return this.client_.unaryCall(this.hostname_ + "/icemdatamonitor.v1.TelemetryService/NavigateUi", request, metadata || {}, this.methodDescriptorNavigateUi);
  }
}

// src/api.ts
var import_telemetry_pb = __toESM(require_telemetry_pb(), 1);

class HostApi {
  client;
  constructor() {
    this.client = new TelemetryServiceClient(window.location.origin);
  }
  async getStatus() {
    return await this.client.getStatus(new import_telemetry_pb.HostStatusRequest);
  }
  subscribeMetrics(intervalMs, onData, onError, onEnd) {
    const ctrl = new AbortController;
    const req = new import_telemetry_pb.SubscribeRequest;
    req.setIntervalMs(intervalMs);
    const payload = req.serializeBinary();
    const frame = new Uint8Array(5 + payload.length);
    frame[0] = 0;
    frame.set([
      payload.length >>> 24 & 255,
      payload.length >>> 16 & 255,
      payload.length >>> 8 & 255,
      payload.length & 255
    ], 1);
    frame.set(payload, 5);
    (async () => {
      let buf = new Uint8Array(0);
      try {
        const resp = await fetch(`${window.location.origin}/icemdatamonitor.v1.TelemetryService/SubscribeMetrics`, {
          method: "POST",
          headers: {
            "Content-Type": "application/grpc-web+proto",
            "X-Grpc-Web": "1"
          },
          body: frame.buffer,
          signal: ctrl.signal
        });
        if (!resp.ok || !resp.body)
          throw new Error(`HTTP ${resp.status}`);
        const reader = resp.body.getReader();
        for (;; ) {
          const { done, value } = await reader.read();
          if (done)
            break;
          if (!value)
            continue;
          const next = new Uint8Array(buf.length + value.length);
          next.set(buf, 0);
          next.set(value, buf.length);
          buf = next;
          while (buf.length >= 5) {
            const flag = buf[0];
            const len = (buf[1] << 24 | buf[2] << 16 | buf[3] << 8 | buf[4]) >>> 0;
            if (buf.length < 5 + len)
              break;
            const body = buf.subarray(5, 5 + len);
            buf = buf.subarray(5 + len);
            if (flag === 0) {
              onData(import_telemetry_pb.MetricsSample.deserializeBinary(body));
            } else if (flag & 128) {
              const status = parseTrailerStatus(body);
              if (status !== "0" && status !== "") {
                throw new Error(`grpc-status ${status}`);
              }
              return;
            }
          }
        }
        onEnd();
      } catch (err) {
        if (!ctrl.signal.aborted)
          onError(err);
      }
    })();
    return { cancel: () => ctrl.abort() };
  }
}
function parseTrailerStatus(body) {
  let offset = 0;
  const view = new DataView(body.buffer, body.byteOffset, body.byteLength);
  while (offset + 2 <= body.length) {
    if (body[offset] !== 10)
      break;
    const keyLen = body[offset + 1];
    if (keyLen >= 128)
      break;
    const keyStart = offset + 2;
    if (keyStart + keyLen + 2 > body.length)
      break;
    const key = new TextDecoder().decode(body.subarray(keyStart, keyStart + keyLen));
    offset = keyStart + keyLen;
    if (body[offset] !== 18)
      break;
    const valLen = body[offset + 1];
    if (valLen >= 128)
      break;
    const val = new TextDecoder().decode(body.subarray(offset + 2, offset + 2 + valLen));
    if (key === "grpc-status")
      return val;
    offset = offset + 2 + valLen;
  }
  return "";
}

// src/backoff.ts
class ReconnectBackoff {
  static StepsSeconds = [3, 6, 12, 24];
  static CapSeconds = 60;
  static JitterFraction = 0.1;
  attempt = 0;
  state = "Connected" /* Connected */;
  jitter01;
  constructor(jitter01) {
    this.jitter01 = jitter01 ?? (() => Math.random());
  }
  get State() {
    return this.state;
  }
  ReportConnected() {
    this.attempt = 0;
    this.transition("Connected" /* Connected */);
  }
  ReportLost() {
    this.attempt = 0;
    this.transition("Lost" /* Lost */);
  }
  NextDelay() {
    const base = this.attempt < ReconnectBackoff.StepsSeconds.length ? ReconnectBackoff.StepsSeconds[this.attempt] : ReconnectBackoff.CapSeconds;
    this.attempt++;
    if (this.state === "Lost" /* Lost */)
      this.transition("Reconnecting" /* Reconnecting */);
    const f = ReconnectBackoff.JitterFraction;
    const factor = 1 - f + 2 * f * clamp01(this.jitter01());
    return base * factor * 1000;
  }
  transition(next) {
    if (this.state === next)
      return;
    this.state = next;
    this.onStateChanged?.(next);
  }
  onStateChanged;
}
function clamp01(v) {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

// src/chart.ts
var PALETTE = [
  "#CC3300",
  "#339933",
  "#006699",
  "#FF9933",
  "#634B9F",
  "#FFCC05",
  "#D86C27",
  "#009999",
  "#660066",
  "#A1A744"
];
function gridColor() {
  return document.documentElement.getAttribute("data-theme") === "light" ? "#d8d8dc" : "#1e1e3a";
}
function labelColor() {
  return document.documentElement.getAttribute("data-theme") === "light" ? "#666" : "#808080";
}
function paletteColor(idx) {
  return PALETTE[idx % PALETTE.length];
}
function renderChart(canvasId, legendId, datasets) {
  const canvas = document.getElementById(canvasId);
  if (!canvas)
    return;
  const ctx = canvas.getContext("2d");
  if (!ctx)
    return;
  const dpr = window.devicePixelRatio || 1;
  const rect = canvas.getBoundingClientRect();
  canvas.width = Math.max(1, Math.floor(rect.width * dpr));
  canvas.height = Math.max(1, Math.floor(rect.height * dpr));
  ctx.scale(dpr, dpr);
  const w = rect.width;
  const h = rect.height;
  const pad = { top: 10, right: 10, bottom: 28, left: 44 };
  const gw = w - pad.left - pad.right;
  const gh = h - pad.top - pad.bottom;
  ctx.clearRect(0, 0, w, h);
  renderLegend(legendId, datasets);
  if (!datasets || datasets.length === 0)
    return;
  const allTs = [];
  const allV = [];
  for (const ds of datasets)
    for (const p of ds.samples) {
      allTs.push(new Date(p.ts).getTime());
      allV.push(p.v);
    }
  if (allTs.length === 0)
    return;
  let tMin = Math.min(...allTs);
  let tMax = Math.max(...allTs);
  let vMin = Math.min(...allV);
  let vMax = Math.max(...allV);
  if (vMin === vMax) {
    vMin -= 1;
    vMax += 1;
  }
  const tSpan = Math.max(1, tMax - tMin);
  const vSpan = vMax - vMin;
  ctx.strokeStyle = gridColor();
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let i = 0;i <= 4; i++) {
    const y = pad.top + gh * i / 4;
    ctx.moveTo(pad.left, y);
    ctx.lineTo(pad.left + gw, y);
  }
  ctx.stroke();
  ctx.fillStyle = labelColor();
  ctx.font = "10px Segoe UI";
  ctx.textAlign = "right";
  ctx.textBaseline = "middle";
  for (let j = 0;j <= 4; j++) {
    const y = pad.top + gh * j / 4;
    const v = vMax - vSpan * j / 4;
    ctx.fillText(fmtAxis(v), pad.left - 6, y);
  }
  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  const timeLabels = 3;
  for (let k = 0;k <= timeLabels; k++) {
    const x = pad.left + gw * k / timeLabels;
    const t = new Date(tMin + tSpan * k / timeLabels);
    ctx.fillText(t.toLocaleTimeString(), x, h - pad.bottom + 6);
  }
  datasets.forEach((ds, idx) => {
    ctx.strokeStyle = ds.color || paletteColor(idx);
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    let first = true;
    for (const p of ds.samples) {
      const t = new Date(p.ts).getTime();
      const x = pad.left + (t - tMin) / tSpan * gw;
      const y = pad.top + (1 - (p.v - vMin) / vSpan) * gh;
      if (first) {
        ctx.moveTo(x, y);
        first = false;
      } else
        ctx.lineTo(x, y);
    }
    ctx.stroke();
  });
}
function renderLegend(legendId, datasets) {
  const el = document.getElementById(legendId);
  if (!el)
    return;
  el.innerHTML = "";
  datasets.forEach((ds, idx) => {
    const color = ds.color || paletteColor(idx);
    const item = document.createElement("span");
    item.className = "legend-item";
    item.innerHTML = `<span class="legend-dot" style="background:${color}"></span>${esc(ds.label)}`;
    el.appendChild(item);
  });
}
function fmtAxis(v) {
  if (Math.abs(v) >= 1e6)
    return (v / 1e6).toFixed(1) + "M";
  if (Math.abs(v) >= 1000)
    return (v / 1000).toFixed(1) + "k";
  if (Math.abs(v) >= 1)
    return v.toFixed(1);
  return v.toFixed(3);
}
function esc(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c] || c);
}

// src/ui.ts
var pb = __toESM(require_telemetry_pb(), 1);

// src/format.ts
function fmtNum(v) {
  const abs = Math.abs(v);
  if (abs >= 1000)
    return v.toFixed(1);
  return v.toFixed(4).replace(/0+$/, "").replace(/\.$/, "");
}
function fmtAge(ms) {
  if (ms < 0)
    ms = 0;
  if (ms < 60000)
    return (ms / 1000).toFixed(1) + "s";
  const m = Math.floor(ms / 60000);
  const s = Math.floor(ms % 60000 / 1000);
  return `${m}m${String(s).padStart(2, "0")}s`;
}

// src/ui.ts
class DashboardUI {
  metrics = new Map;
  sampleCount = 0;
  lastSampleAt = 0;
  zonesListenerBound = false;
  onRegToggle = null;
  renderStatus(s) {
    this.text("cardBus", s.getBusOpen() ? "открыта" : "закрыта");
    this.lamp("cardBusLamp", s.getBusOpen() ? "on" : "off");
    this.text("cardAddin", s.getAddinAlive() ? "жив" : "нет");
    this.lamp("cardAddinLamp", s.getAddinAlive() ? "on" : "off");
    const zoneNames = {
      [pb.BusZoneKind.BUS_ZONE_COM]: "com",
      [pb.BusZoneKind.BUS_ZONE_LAN]: "lan",
      [pb.BusZoneKind.BUS_ZONE_RAW]: "raw"
    };
    const zoneRows = s.getZonesList().map((z) => {
      const name = zoneNames[z.getKind()] ?? `kind=${z.getKind()}`;
      return `<tr><td>${name}</td>` + `<td><span class="badge ${z.getActive() ? "green" : "red"}">${z.getActive() ? "live" : "stale"}</span></td>` + `<td class="mono">${fmtAge(z.getAgeMs())}</td></tr>`;
    });
    this.tbody("zonesTable", zoneRows.length ? zoneRows.join("") : '<tr><td colspan="3" class="muted">нет данных зон</td></tr>');
    const layers = s.getLayers();
    if (layers) {
      const chip = (label, on) => `<span class="badge ${on ? "green" : "grey-chip"}">${label}: ${on ? "вкл" : "выкл"}</span>`;
      document.getElementById("layerChips").innerHTML = chip("modbus", layers.getModbus()) + chip("web", layers.getWeb()) + chip("sniffer", layers.getSniffer());
      this.text("layerError", layers.getLastError() || "");
    }
  }
  renderSummaryLamps(s) {
    this.lamp("lampIcem", s.busOpen ? s.addInAlive ? "on" : "warn" : "off");
    this.lamp("lampCom", s.zones?.com?.active ? "on" : "off");
    this.lamp("lampLan", s.zones?.lan?.active ? "on" : "off");
    this.lamp("lampRaw", s.zones?.raw?.active ? "on" : "off");
    this.lamp("lampModbus", s.layers?.modbus ? "on" : "off");
    this.lamp("lampSniffer", s.layers?.sniffer ? "on" : "off");
    this.lamp("lampOpc", s.layers?.opcDa ? "on" : "grey");
  }
  setServerLamp(state) {
    this.lamp("lampSrv", state);
  }
  applySample(sample) {
    this.sampleCount++;
    this.lastSampleAt = Date.now();
    for (const d of sample.getDeltasList()) {
      const kind = d.getValueCase() === pb.RegisterDelta.ValueCase.FLOAT_VALUE ? "float" : "int32";
      const value = kind === "float" ? d.getFloatValue() : d.getInt32Value();
      this.metrics.set(d.getAddr(), { value, kind, updatedAt: this.lastSampleAt });
    }
    this.pulse("pulseLamp");
    this.pulse("cardHeartbeatLamp");
    this.text("pulseHb", String(sample.getHeartbeat()));
    this.text("cardHeartbeat", String(sample.getHeartbeat()));
    const clamped = document.getElementById("clampedBadge");
    clamped.classList.toggle("hidden", !sample.getIntervalClamped());
    this.text("metricsMeta", `регистров: ${this.metrics.size} · сэмплов: ${this.sampleCount}`);
    this.renderMetricsTable();
  }
  renderMetricsTable() {
    const rows = [...this.metrics.entries()].sort((a, b) => a[0] - b[0]).map(([addr, m]) => {
      const stale = Date.now() - m.updatedAt > 1e4;
      const value = m.kind === "float" ? fmtNum(m.value) : String(m.value);
      return `<tr${stale ? ' class="stale-warn"' : ""}>` + `<td class="r-addr mono">${addr}</td>` + `<td class="r-value">${value}</td>` + `<td class="r-kind">${m.kind}</td>` + `<td class="r-age mono">${fmtAge(Date.now() - m.updatedAt)} назад</td></tr>`;
    });
    this.tbody("metricsTable", rows.join("") || '<tr><td colspan="4" class="muted empty-row">дельт пока нет</td></tr>');
  }
  refreshAges() {
    if (this.metrics.size > 0)
      this.renderMetricsTable();
    if (this.lastSampleAt > 0)
      this.text("lastSampleAt", `последний сэмпл: ${fmtAge(Date.now() - this.lastSampleAt)} назад`);
  }
  get lastSampleMs() {
    return this.lastSampleAt;
  }
  renderRegs(data) {
    const byReg = new Map(data.system.scalars.map((s) => [s.reg, s]));
    const com = byReg.get(0);
    if (com) {
      this.text("cardComPort", String(com.value));
      this.text("cardComPortSub", com.label ?? "com_port");
    }
    const caseState = byReg.get(1);
    if (caseState) {
      this.text("cardCaseState", caseState.label ?? String(caseState.value));
    }
    const lanState = byReg.get(2);
    if (lanState) {
      this.text("cardCaseLan", lanState.label ?? String(lanState.value));
    }
    const zonesEl = document.getElementById("zones");
    if (!zonesEl)
      return;
    const parts = [];
    parts.push(zoneCard("System [0..3]", null, regTable(data.system.scalars.map((s) => `<tr><td class="r-eye muted">—</td>` + `<td class="r-idx muted">—</td>` + `<td class="r-addr mono">${s.reg}</td>` + `<td class="r-name">${esc2(s.name)}</td>` + `<td class="r-value">${esc2(String(s.value))}${s.label ? stateLabel(s.label) : ""}</td></tr>`).join("")), true));
    for (const key of ["com", "lan", "raw"]) {
      const zone = data.zones?.[key];
      if (!zone)
        continue;
      const live = data.liveness?.[key];
      const rows = zone.pairs.map((p) => `<tr><td class="r-eye"><input type="checkbox" data-addr="${p.addr}" title="Строить график этого регистра"${this.checkedAddrs.has(p.addr) ? " checked" : ""}></td>` + `<td class="r-idx muted">${p.idx}</td>` + `<td class="r-addr mono">${p.addr}..${p.addrEnd}</td>` + `<td class="r-name">${p.name ? esc2(p.name) : '<span class="muted">—</span>'}</td>` + `<td class="r-value">${formatValue(p)}</td></tr>`).join("");
      const title = `${zone.label} [${zone.baseReg}..${zone.baseReg + zone.len - 1}]`;
      parts.push(zoneCard(title, live ?? null, regTable(rows), false));
    }
    zonesEl.innerHTML = parts.join("");
    if (!this.zonesListenerBound && this.onRegToggle) {
      this.zonesListenerBound = true;
      zonesEl.addEventListener("change", (ev) => {
        const target = ev.target;
        if (target?.type !== "checkbox" || !target.dataset.addr)
          return;
        this.onRegToggle(Number(target.dataset.addr), target.checked);
      });
    }
  }
  setCheckedAddrs(addrs) {
    this.checkedAddrs = new Set(addrs);
  }
  renderSelectedTable(data, selected) {
    const byAddr = new Map;
    for (const key of ["com", "lan", "raw"])
      for (const p of data.zones?.[key]?.pairs ?? [])
        byAddr.set(p.addr, p);
    const rows = [...selected].sort((a, b) => a - b).map((addr) => {
      const p = byAddr.get(addr);
      if (!p)
        return "";
      return `<tr><td class="r-addr mono">${addr}</td>` + `<td class="r-name">${p.name ? esc2(p.name) : '<span class="muted">—</span>'}</td>` + `<td class="r-value">${formatValue(p)}</td></tr>`;
    }).join("");
    this.tbody("selTable", rows || '<tr><td colspan="3" class="muted empty-row">нет отмеченных переменных</td></tr>');
  }
  checkedAddrs = new Set;
  lamp(id, state) {
    const el = req(id);
    el.className = `lamp-dot ${state}` + (el.classList.contains("pulse") ? " pulse" : "");
  }
  pulse(id) {
    const el = req(id);
    el.classList.remove("on");
    el.offsetWidth;
    el.classList.add("on");
  }
  text(id, value) {
    req(id).textContent = value;
  }
  tbody(tableId, html) {
    req(tableId).querySelector("tbody").innerHTML = html;
  }
}
function zoneCard(title, live, tableHtml, noEyeCol) {
  const status = live ? live.active ? `<span class="badge green">live ${esc2(fmtAge(live.ageMs))}</span>` : `<span class="badge red">stale ${esc2(fmtAge(live.ageMs))}</span>` : "";
  return `<div class="zone-card"><div class="zone-header">` + `<span class="zone-title">${esc2(title)}</span>${status}</div>` + tableHtml + `</div>`;
}
function regTable(rowsHtml) {
  const eyeTh = '<th class="r-eye" title="график"></th>';
  return `<table class="reg-table"><thead><tr>` + `${eyeTh}<th class="r-idx">idx</th><th class="r-addr">addr</th>` + `<th class="r-name">name</th><th class="r-value-h">value</th>` + `</tr></thead><tbody>${rowsHtml}</tbody></table>`;
}
function stateLabel(label) {
  return ` <span class="state-label" title="семантическое состояние">${esc2(label)}</span>`;
}
function formatValue(p) {
  if (p.kind === "unix")
    return esc2(String(p.value));
  const v = Number(p.value);
  if (!isFinite(v))
    return esc2(String(p.value));
  if (Math.abs(v) >= 1e4 || Math.abs(v) < 0.001 && v !== 0)
    return esc2(v.toExponential(3));
  return esc2(v.toFixed(Math.abs(v) >= 100 ? 1 : 3));
}
function esc2(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c] || c);
}
function req(id) {
  const el = document.getElementById(id);
  if (!el)
    throw new Error(`missing #${id} in index.html`);
  return el;
}

// src/main.ts
var STALE_MS = 3000;
var STATUS_POLL_MS = 5000;
var WATCHDOG_MS = 1000;
var REGS_POLL_MS = 1000;
var SUMMARY_POLL_MS = 3000;
var ui = new DashboardUI;
var APP_VERSION = "32";
{
  const htmlVersion = document.documentElement.getAttribute("data-app-version");
  if (htmlVersion !== APP_VERSION) {
    if (!sessionStorage.getItem("icem.versionReloaded")) {
      sessionStorage.setItem("icem.versionReloaded", "1");
      location.replace(`${location.pathname}?v=${APP_VERSION}&t=${Date.now()}`);
    }
    throw new Error(`stale index.html (dom=${htmlVersion}, js=${APP_VERSION})`);
  }
  sessionStorage.removeItem("icem.versionReloaded");
}
localStorage.removeItem("icem.host.token");
function tickClock() {
  document.getElementById("clock").textContent = new Date().toLocaleString();
}
tickClock();
setInterval(tickClock, 1000);
var THEME_KEY = "icem.host.theme";
function applyTheme(theme) {
  document.documentElement.setAttribute("data-theme", theme);
  document.querySelector("#themeToggle use")?.setAttribute("href", theme === "dark" ? "#ic-sun" : "#ic-moon");
  localStorage.setItem(THEME_KEY, theme);
}
applyTheme(localStorage.getItem(THEME_KEY) ?? "dark");
document.getElementById("themeToggle").addEventListener("click", () => applyTheme(document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark"));

class ConnectionManager {
  api = null;
  stream = null;
  backoff = new ReconnectBackoff;
  reconnectTimer = null;
  polls = [];
  lastDataAt = 0;
  regsFailCount = 0;
  start() {
    this.stop();
    this.api = new HostApi;
    this.lastDataAt = Date.now();
    this.openStream();
    this.refreshStatus();
    this.polls.push(setInterval(() => void this.refreshStatus(), STATUS_POLL_MS));
    this.polls.push(setInterval(() => this.watchdog(), WATCHDOG_MS));
    this.polls.push(setInterval(() => void this.pollRegs(), REGS_POLL_MS));
    this.polls.push(setInterval(() => void this.pollSummary(), SUMMARY_POLL_MS));
    this.pollRegs();
    this.pollSummary();
  }
  stop() {
    this.stream?.cancel();
    this.stream = null;
    if (this.reconnectTimer !== null)
      clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this.polls.forEach(clearInterval);
    this.polls = [];
    this.backoff = new ReconnectBackoff;
  }
  openStream() {
    if (!this.api)
      return;
    this.stream?.cancel();
    this.stream = this.api.subscribeMetrics(1000, (sample) => this.onSample(sample), (err) => this.onStreamFailure(err), () => this.onStreamFailure(new Error("stream ended by server")));
  }
  onSample(sample) {
    this.lastDataAt = Date.now();
    ui.applySample(sample);
    if (this.backoff.State !== "Connected") {
      this.backoff.ReportConnected();
      if (this.reconnectTimer !== null) {
        clearTimeout(this.reconnectTimer);
        this.reconnectTimer = null;
      }
    }
  }
  onStreamFailure(_err) {
    if (this.reconnectTimer !== null)
      return;
    if (this.backoff.State === "Connected")
      this.backoff.ReportLost();
    const delayMs = this.backoff.NextDelay();
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.openStream();
    }, delayMs);
  }
  watchdog() {
    ui.refreshAges();
    const age = Date.now() - this.lastDataAt;
    if (age > STALE_MS && this.reconnectTimer === null && this.backoff.State === "Connected")
      this.onStreamFailure(new Error(`нет данных ${Math.round(age / 1000)}s`));
  }
  async refreshStatus() {
    if (!this.api)
      return;
    try {
      const status = await this.api.getStatus();
      ui.renderStatus(status);
    } catch (err) {
      console.warn("[conn] GetStatus:", err.message);
    }
  }
  async pollRegs() {
    try {
      const res = await fetch("/api/regs", { cache: "no-store" });
      if (!res.ok)
        throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      ui.setCheckedAddrs(selected);
      ui.renderRegs(data);
      ui.renderSelectedTable(data, selected);
      for (const key of ["com", "lan", "raw"])
        for (const p of data.zones?.[key]?.pairs ?? [])
          if (p.name)
            nameByAddr.set(p.addr, p.name);
      this.regsFailCount = 0;
      ui.setServerLamp("on");
      if (selected.size > 0)
        refreshChart();
    } catch (err) {
      if (++this.regsFailCount >= 2)
        ui.setServerLamp("off");
      console.warn("[conn] /api/regs:", err.message);
    }
  }
  async pollSummary() {
    try {
      const res = await fetch("/api/status/summary", { cache: "no-store" });
      if (!res.ok)
        throw new Error(`HTTP ${res.status}`);
      ui.renderSummaryLamps(await res.json());
    } catch (err) {
      console.warn("[conn] /api/status/summary:", err.message);
    }
  }
}
var conn = new ConnectionManager;
var SEL_KEY = "regs.selected";
var CHART_EMPTY_HINT = "Отметьте регистры чекбоксами, чтобы строить историю";
var selected = new Set(loadSelection());
var nameByAddr = new Map;
function loadSelection() {
  try {
    const raw = localStorage.getItem(SEL_KEY);
    if (raw)
      return JSON.parse(raw);
  } catch {}
  return [];
}
function saveSelection() {
  try {
    localStorage.setItem(SEL_KEY, JSON.stringify([...selected]));
  } catch {}
}
function syncSelectionUi() {
  document.getElementById("selCount").textContent = `${selected.size} выбрано`;
  const empty = document.getElementById("chartEmpty");
  if (selected.size === 0) {
    empty.textContent = CHART_EMPTY_HINT;
    empty.style.display = "flex";
  } else {
    empty.style.display = "none";
  }
}
async function refreshChart() {
  if (selected.size === 0) {
    renderChart("chartCanvas", "chartLegend", []);
    return;
  }
  const qs = [...selected].map((a) => `addr=${a}`).join("&");
  try {
    const res = await fetch(`/api/regs/history?${qs}`, { cache: "no-store" });
    if (!res.ok)
      throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    const datasets = [];
    let idx = 0;
    for (const addr of selected) {
      // /api/regs/history returns a FLAT addr-keyed dict { "100": [...], ... }
      // (guard: iCemDataMonitor.Host.Tests WwwrootHistoryContractGuardTests).
      const samples = data?.[String(addr)] ?? [];
      if (samples.length === 0)
        continue;
      datasets.push({
        label: nameByAddr.get(addr) ?? `addr ${addr}`,
        color: paletteColor(idx),
        samples
      });
      idx++;
    }
    document.getElementById("chartEmpty").style.display = "none";
    renderChart("chartCanvas", "chartLegend", datasets);
  } catch (err) {
    console.warn("[regs] history:", err.message);
    // Silent-blank lesson (2026-08): a failed history fetch must be VISIBLE,
    // not console-only — reuse the chart-empty placeholder as the status line.
    const empty = document.getElementById("chartEmpty");
    if (empty) {
      empty.textContent = `Ошибка загрузки истории: ${err.message}`;
      empty.style.display = "flex";
    }
  }
}
ui.onRegToggle = (addr, checked) => {
  if (checked)
    selected.add(addr);
  else
    selected.delete(addr);
  saveSelection();
  syncSelectionUi();
  refreshChart();
};
document.getElementById("btnClear")?.addEventListener("click", () => {
  selected.clear();
  saveSelection();
  ui.setCheckedAddrs(selected);
  syncSelectionUi();
  renderChart("chartCanvas", "chartLegend", []);
});
{
  const viewMain = document.getElementById("viewMain");
  const viewStatus = document.getElementById("viewStatus");
  const btnStatus = document.getElementById("btnStatus");
  const VIEW_KEY = "icem.view";
  const applyView = (statusVisible) => {
    viewMain?.classList.toggle("hidden", statusVisible);
    viewStatus?.classList.toggle("hidden", !statusVisible);
    btnStatus?.classList.toggle("active", statusVisible);
  };
  btnStatus?.addEventListener("click", () => {
    const goingToStatus = viewStatus?.classList.contains("hidden") ?? true;
    applyView(goingToStatus);
    try {
      localStorage.setItem(VIEW_KEY, goingToStatus ? "status" : "main");
    } catch {}
  });
  applyView(false);
  try {
    if (localStorage.getItem(VIEW_KEY) === "status")
      applyView(true);
  } catch {}
}
window.addEventListener("resize", () => void refreshChart());
ui.setCheckedAddrs(selected);
syncSelectionUi();
conn.start();
