﻿using System.Collections.Generic;
using MultiThreadedReactiveUI.DataProvider;
using MultiThreadedReactiveUI.Model;

namespace MultiThreadedReactiveUI.DataProvider
{
    public class FunctionDataProvider : IFunctionDataProvider
    {
        public IEnumerable<Model.Function> LoadFunctions()
        {
            List<Function> functionsToReturn = new List<Function>();
            functionsToReturn.Add(new SinFunction());
            functionsToReturn.Add(new CosFunction());
            return functionsToReturn;

        }
    }
}
