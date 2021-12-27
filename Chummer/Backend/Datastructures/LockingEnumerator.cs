/*  This file is part of Chummer5a.
 *
 *  Chummer5a is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  Chummer5a is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with Chummer5a.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  You can obtain the full source code for Chummer5a at
 *  https://github.com/chummer5a/chummer5a
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Chummer
{
    /// <summary>
    /// Special class that should be used instead of GetEnumerator for collections with an internal locking object
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public readonly struct LockingEnumerator<T> : IEnumerator<T>, IEquatable<LockingEnumerator<T>>
    {
        private readonly ReaderWriterLockSlim _rwlThis;

        private readonly IEnumerator<T> _objInternalEnumerator;

        public LockingEnumerator(IEnumerable<T> lstEnumerable, ReaderWriterLockSlim rwlThis)
        {
            _objInternalEnumerator = lstEnumerable.GetEnumerator();
            _rwlThis = rwlThis;
        }

        public LockingEnumerator(IEnumerator<T> lstEnumerator, ReaderWriterLockSlim rwlThis)
        {
            _objInternalEnumerator = lstEnumerator;
            _rwlThis = rwlThis;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            using (new EnterReadLock(_rwlThis))
                _objInternalEnumerator.Dispose();
        }

        /// <inheritdoc />
        public bool MoveNext()
        {
            using (new EnterReadLock(_rwlThis))
                return _objInternalEnumerator.MoveNext();
        }

        /// <inheritdoc />
        public void Reset()
        {
            using (new EnterReadLock(_rwlThis))
                _objInternalEnumerator.Reset();
        }

        /// <inheritdoc />
        public T Current
        {
            get
            {
                using (new EnterReadLock(_rwlThis))
                    return _objInternalEnumerator.Current;
            }
        }

        /// <inheritdoc />
        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool Equals(LockingEnumerator<T> other)
        {
            return Equals(_rwlThis, other._rwlThis) && Equals(_objInternalEnumerator, other._objInternalEnumerator);
        }

        public override bool Equals(object obj)
        {
            return obj != null && Equals((LockingEnumerator<T>)obj);
        }

        public override int GetHashCode()
        {
            return (_rwlThis, _objInternalEnumerator).GetHashCode();
        }

        public override string ToString()
        {
            return _objInternalEnumerator.ToString() + ' ' + _rwlThis;
        }

        public static bool operator ==(LockingEnumerator<T> left, LockingEnumerator<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(LockingEnumerator<T> left, LockingEnumerator<T> right)
        {
            return !(left == right);
        }
    }
}
