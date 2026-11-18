using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;

namespace Mason {
  public sealed class NavigationIcon : FrameworkElement {
    private static readonly Dictionary<string,Geometry[]> cache=new Dictionary<string,Geometry[]>();
    private readonly Geometry[] shapes;
    private readonly Brush fill;
    public const double BoxSize=24,InkSize=22;
    public NavigationIcon(string key,Brush color){
      Width=BoxSize;Height=BoxSize;HorizontalAlignment=HorizontalAlignment.Center;VerticalAlignment=VerticalAlignment.Center;fill=color;
      string name=key=="maintenance"?"input":key;
      if(!cache.TryGetValue(name,out shapes)){
        string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"icons",name+".svg");
        var document=new XmlDocument {XmlResolver=null};
        using(var reader=XmlReader.Create(file,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))document.Load(reader);
        var items=new List<Geometry>();
        foreach(XmlNode node in document.GetElementsByTagName("path")){
          string data=node.Attributes["d"].Value;Geometry geometry=Geometry.Parse("F1 "+data);geometry.Freeze();items.Add(geometry);
        }
        if(items.Count==0)throw new InvalidDataException("导航图标没有矢量路径。");
        shapes=items.ToArray();cache[name]=shapes;
      }
      IsHitTestVisible=false;SnapsToDevicePixels=false;RenderOptions.SetEdgeMode(this,EdgeMode.Unspecified);
    }
    public Rect SourceBounds {get{Rect bounds=Rect.Empty;foreach(var shape in shapes)bounds.Union(shape.Bounds);return bounds;}}
    protected override void OnRender(DrawingContext dc){
      Rect bounds=SourceBounds;double scale=Math.Min(InkSize/bounds.Width,InkSize/bounds.Height);
      var transform=new MatrixTransform(scale,0,0,scale,(ActualWidth-bounds.Width*scale)/2-bounds.X*scale,(ActualHeight-bounds.Height*scale)/2-bounds.Y*scale);
      dc.PushTransform(transform);foreach(var shape in shapes)dc.DrawGeometry(fill,null,shape);dc.Pop();
    }
  }
}
