using System;
using System.Windows;

namespace LoveMyShelf {
    // The shapes of covers and of the grid's cells.
    // A cover keeps the shape it was cut in (any shape since 1.0.9; 9:16 before). The cells of the grid have one shape
    // the person picks (9:16, 3:4 or 1:1) and a cover is fitted whole inside its cell, so changing the cells never
    // calls for making the covers again. A cover clearly wider than a cell takes two cells side by side (an option).
    internal static class Covers {
        internal const double Tall=9.0/16;
        // Width over height of each cell shape, in the order the options list them.
        internal static readonly double[] Shapes={9.0/16,3.0/4,1.0};
        internal static readonly string[] ShapeKeys={"9:16","3:4","1:1"};
        internal const int DefaultShape=1; // a new install starts with 3:4 (picked on the first screen)
        // The narrowest and widest shape a cover can be cut in (1:5 to 5:1).
        internal const double Narrowest=0.2,Widest=5;

        internal static double Shape(int shape) {return Shapes[Math.Max(0,Math.Min(Shapes.Length-1,shape))];}
        internal static string ShapeLabel(int shape) {
            switch(shape) {
                case 0:return L.P("9:16 tall","9:16 세로");
                case 2:return L.P("1:1 square","1:1 정사각형");
                default:return L.P("3:4 tall","3:4 세로");
            }
        }
        // A saved shape that can't be right (missing, zero, endless) reads as the old 9:16. Any real shape stays,
        // a 6:1 panorama kept whole included.
        internal static double Valid(double? aspect) {
            if(!aspect.HasValue || Double.IsNaN(aspect.Value) || Double.IsInfinity(aspect.Value))return Tall;
            return aspect.Value>=0.01 && aspect.Value<=100?aspect.Value:Tall;
        }
        // The shape in pixels of a cover cut as these fractions of a picture this big.
        // Any real shape is kept as it is, so the editor, the saved record and the small copy agree.
        internal static double AspectOf(Rect fraction,double width,double height) {
            if(fraction.Width<=0 || fraction.Height<=0 || width<=0 || height<=0)return Tall;
            double aspect=fraction.Width*width/(fraction.Height*height);
            return aspect>0 && !Double.IsNaN(aspect) && !Double.IsInfinity(aspect)?aspect:Tall;
        }
        // Two cells side by side show a cover bigger than one cell does once the cover is wider than the
        // middle shape between the two (the cell's shape times the square root of 2): 9:16 cells → wider than about 4:5,
        // 3:4 cells → wider than about 1.06:1, square cells → wider than about 1.41:1.
        internal static bool TakesTwoCells(double aspect,double cell) {return aspect>cell*Math.Sqrt(2)+1e-9;}
        // How much of a box a picture of this shape fills when fitted whole inside it (0..1).
        internal static double Fill(double aspect,double box) {return aspect>box?box/aspect:aspect/box;}
        // A shape as "16:9", "4:3", "1:1" when it is a common one, else as "1.85:1" / "1:2.10".
        internal static string Name(double aspect) {
            foreach(var pair in new[]{new[]{9,16},new[]{3,4},new[]{4,5},new[]{2,3},new[]{1,1},new[]{5,4},new[]{4,3},new[]{3,2},new[]{16,9},new[]{21,9},new[]{1,2},new[]{2,1}})
                if(Math.Abs(aspect-pair[0]/(double)pair[1])<0.008*aspect)return pair[0]+":"+pair[1];
            var c=System.Globalization.CultureInfo.CurrentCulture;
            return aspect>=1?aspect.ToString("0.##",c)+":1":"1:"+(1/aspect).ToString("0.##",c);
        }
    }
}
