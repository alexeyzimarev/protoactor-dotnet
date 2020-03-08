// -----------------------------------------------------------------------
//   <copyright file="Log.cs" company="Asynkron HB">
//       Copyright (C) 2015-2018 Asynkron HB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace Proto
{
    public static class Log
    {
        private static ILoggerFactory loggerFactory = new NullLoggerFactory();

        public static void SetLoggerFactory(ILoggerFactory factory) => loggerFactory = factory;

        public static ILogger CreateLogger(string categoryName) => loggerFactory.CreateLogger(categoryName);

        public static ILogger<T> CreateLogger<T>() => loggerFactory.CreateLogger<T>();
    }
}