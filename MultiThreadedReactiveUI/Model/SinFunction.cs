﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiThreadedReactiveUI.Model
{
    class SinFunction : Function
    {
        public string Category
        {
            get
            {
                return Constants.CategoryTrig;
            }
        }

        public string DisplayName
        {
            get
            {
                return Constants.FuncitonSin;
            }
        }

        public double FunctionToRun(double parameter)
        {
            return Math.Sin(parameter);
        }
    }
}
