﻿/*
 * Copyright © 2017 Jesse Nicholson
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;

namespace DistillNET.Extensions
{
    internal static class StringExtensions
    {
        /// <summary>
        /// Determines if this string begins with the same character sequence as the supplied string.
        /// </summary>
        /// <param name="str">
        /// This string.
        /// </param>
        /// <param name="other">
        /// The string to compare the beginning of this
        /// </param>
        /// <returns>
        /// </returns>
        /// <remarks>
        /// This method is used rather than the built in .net equivalent for the sake of speed. The
        /// built in method invokes various internationalization methods inside the .net framework
        /// that simply are not necessary for our purposes.
        /// </remarks>
        public static bool StartsWithQuick(this string str, string other)
        {
            var len = str.Length;
            var olen = other.Length;

            if(len == 0 || len < olen)
            {
                return false;
            }

            if(str[0] != other[0])
            {
                return false;
            }

            var s = 1;

            while(s < olen)
            {
                if(str[s] != other[s])
                {
                    return false;
                }
                ++s;
            }

            return true;
        }

        /// <summary>
        /// Gets the next position of a character that would indicate the end of an address or domain
        /// anchor string, if any.
        /// </summary>
        /// <param name="str">
        /// This string.
        /// </param>
        /// <param name="startIndex">
        /// The index to begin searching at.
        /// </param>
        /// <returns>
        /// A positive value indicating the index of a matched character in the event that one is
        /// found, a negative number in the event that no such character is found.
        /// </returns>
        public static int IndexOfAnchorEnd(this string str, int startIndex = 0)
        {
            var len = str.Length;

            if(startIndex >= len)
            {
                return -1;
            }

            do
            {
                switch(str[startIndex])
                {
                    case '/':
                    case ':':
                    case '?':
                    case '=':
                    case '&':
                    case '*':
                    case '^':
                    {
                        return startIndex;
                    }
                }

                ++startIndex;
            }
            while(startIndex < len);

            return -1;
        }

        public static int IndexOfQuick(this string str, char what, int startIndex = 0)
        {
            var len = str.Length;

            if(startIndex >= len)
            {
                return -1;
            }

            do
            {
                if(str[startIndex] == what)
                {
                    return startIndex;
                }

                ++startIndex;
            }
            while(startIndex < len);

            return -1;
        }

        public static int IndexOfQuick(this string str, string what, int startIndex = 0)
        {
            var len = str.Length;
            var whatLen = what.Length;

            if(startIndex > len || startIndex + whatLen > len)
            {
                return -1;
            }
            
            do
            {
                if(str[startIndex] == what[0])
                {   
                    if(str.EqualsAt(what, startIndex))
                    {
                        return startIndex;
                    }
                }

                ++startIndex;
            }
            while(startIndex + whatLen < len);

            return -1;
        }

        private static bool EqualsAt(this string str, string what, int index)
        {
            
            var len = str.Length;
            var whatLen = what.Length;
            if(index + whatLen > len)
            {   
                
                return false;
            }

            var relOffset = 0;

            do
            {
                if(str[index + relOffset] != what[relOffset])
                {
                    return false;
                }
                ++relOffset;
            }
            while(relOffset < whatLen);

            return true;
        }

        public static int LastIndexOfQuick(this string str, string what)
        {
            var whatLen = what.Length;
            var thisLen = str.Length;
            var startOffset = thisLen - whatLen;
            if(thisLen == 0 || startOffset < 0)
            {
                return -1;
            }

            switch(whatLen)
            {
                case 3:
                {
                    do
                    {
                        if(str[startOffset] != what[0])
                        {
                            --startOffset;
                            continue;
                        }

                        if(str[startOffset + 1] != what[1])
                        {
                            --startOffset;
                            continue;
                        }

                        if(str[startOffset + 2] != what[2])
                        {
                            --startOffset;
                            continue;
                        }

                        return startOffset;
                    }
                    while(startOffset > -1);

                    return -1;
                }

                case 2:
                {
                    do
                    {
                        if(str[startOffset] != what[0])
                        {
                            --startOffset;
                            continue;
                        }

                        if(str[startOffset + 1] != what[1])
                        {
                            --startOffset;
                            continue;
                        }

                        return startOffset;
                    }
                    while(startOffset > -1);

                    return -1;
                }

                case 1:
                {
                    do
                    {
                        if(str[startOffset] != what[0])
                        {
                            --startOffset;
                            continue;
                        }

                        return startOffset;
                    }
                    while(startOffset > -1);

                    return -1;
                }

                default:
                {
                    throw new ArgumentException("String length is unsupported.", nameof(what));
                }
            }
        }
    }
}