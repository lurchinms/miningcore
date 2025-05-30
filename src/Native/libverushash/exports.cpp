/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#include "verushashverify.h"

#ifdef _WIN32
#define MODULE_API __declspec(dllexport)
#else
#define MODULE_API
#endif

extern "C" MODULE_API void verushash2b2_export(char* input, char* output, int input_length)
{
    verushash2b2(input, output, input_length);
}

extern "C" MODULE_API void verushash2b2o_export(char* input, char* output, int input_length)
{
    verushash2b2o(input, output, input_length);
}

extern "C" MODULE_API void verushash2b1_export(char* input, char* output, int input_length)
{
    verushash2b1(input, output, input_length);
}

extern "C" MODULE_API void verushash2b_export(char* input, char* output, int input_length)
{
    verushash2b(input, output, input_length);
}

extern "C" MODULE_API void verushash2_export(char* input, char* output, int input_length)
{
    verushash2(input, output, input_length);
}

extern "C" MODULE_API void verushash_export(char* input, char* output, int input_length)
{
    verushash(input, output, input_length);
}