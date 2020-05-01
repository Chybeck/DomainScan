using System.Collections.Generic;
using System.Linq;

namespace RRQueue
{
    public class RRQueue : Queue<double>
    {

        public int capacity;

        public RRQueue(int _capacity)
        {
            capacity = _capacity;
        }

        public new void Enqueue(double item)
        {
            base.Enqueue(item);
            if (this.Count > capacity) this.Dequeue();
        }

        public double Average
        {
            get
            {
                return this.Average();
            }
        }
    }
}
