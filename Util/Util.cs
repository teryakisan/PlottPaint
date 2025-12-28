using Microsoft.Win32;
using NVSPlotter.Models;
using NVSPlotter.Properties;
using NVSPlotter.Services;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace NVSPlotter.Util
{
    internal class Utility
    {
        public static double Distance(PointMm a, PointMm b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static double NormalizeAngle(double angle)
        {
            var normalized = angle % 360.0;
            if (normalized <= -180.0) normalized += 360.0;
            if (normalized > 180.0) normalized -= 360.0;
            return normalized;
        }

        public static Vector RotateVector(Vector v, double angleDegrees)
        {
            var radians = angleDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }



    }
}
