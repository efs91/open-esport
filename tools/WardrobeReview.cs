using Godot;
using OpenEsport.Core;
using System;
public partial class WardrobeReview : Node3D
{
    public override async void _Ready()
    {
        try {
            AddChild(new WorldEnvironment { Environment=new Godot.Environment{BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color("43505c"),AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=.83f} });
            AddChild(new DirectionalLight3D{RotationDegrees=new(-38,145,0),LightEnergy=1.3f});
            var cam=new Camera3D{Position=new(.38f,1.10f,-3.7f),Fov=32};AddChild(cam);cam.LookAt(new Vector3(0,.94f,0));
            var a=new CharacterAppearance();
            var actor=new CharacterAnimator{Appearance=a};AddChild(actor);actor.SetModelingPose(true);
            var outfits=CharacterKit.Items("clothes");var hats=CharacterKit.Items("headwear");
            int checkedCount=0;
            for(int i=0;i<outfits.Count;i++){
                var item=outfits[i];if(!item.Id.StartsWith("c_new_"))continue;
                string slug=item.Id.Substring(6);
                int hat=hats.FindIndex(it=>it.Id=="gear_"+slug);
                for(int k=0;k<2;k++){
                    a.Pieces["clothes"]=i;a.Pieces["headwear"]=hat;a.Pieces["eyebrows"]=1;
                    a.Skin=k==1?6:1;a.Gender=k;a.Weight=k==1?-.6f:.65f;
                    a.Details.Clear();if(k==1){CharacterDetails.LoadCatalog();a.SetDetail(CharacterDetails.Dials.Find(d=>d.Id.StartsWith("head/head-scale-depth")).Id,.4f);}
                    var w=new BitWriter();Messages.WriteJoin(w,"Test",a.Pack());var r=new BitReader(w.Buffer,0,w.ByteLength);r.ReadByte();r.ReadUInt16();r.ReadString();
                    var payload=Messages.ReadAppearance(r);w.Reset();Messages.WriteRoster(w,new(){new RosterEntry{Id=1,Name="Test",Appearance=payload}});r.Init(w.Buffer,0,w.ByteLength);r.ReadByte();
                    var b=CharacterAppearance.Unpack(Messages.ReadRoster(r)[0].Appearance);
                    if(b.Piece("headwear")!=hat||b.Piece("clothes")!=i||b.Piece("eyebrows")!=1)throw new Exception("network");
                    actor.SetAppearance(b);
                    var mi=actor.FindChild(item.Id,true,false) as MeshInstance3D;
                    if(mi==null||!mi.Visible||mi.Mesh.GetSurfaceCount()!=1)throw new Exception(slug);
                    actor.SetModelingPose(k==0);
                    for(int n=0;n<(k==0?2:15);n++){
                        if(k==1)actor.Tick(new Vector3(0,0,4),true,false,1.0/60);
                        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                    }
                    await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                    GetViewport().GetTexture().GetImage().SavePng("res://.godot/pose-review/new-"+slug+"-"+k+".png");
                    b.Pieces["headwear"]=-1; actor.SetAppearance(b);
                    if(hat>=0 && actor.FindChild(hats[hat].Id,true,false)!=null)throw new Exception("remove");
                }
                checkedCount++;
            }
            if(checkedCount!=10)throw new Exception("count "+checkedCount);
            for(int h=-1;h<hats.Count;h++) {
                a.Pieces["clothes"]=outfits.FindIndex(o=>o.Id=="c_new_harley_quinn");
                a.Pieces["headwear"]=h;
                var saved=CharacterAppearance.FromDict(a.ToDict());
                if(saved.Piece("headwear")!=h || saved.Piece("clothes")!=a.Piece("clothes"))throw new Exception("save");
                actor.SetAppearance(saved);
                if(h>=0 && !((MeshInstance3D)actor.FindChild(hats[h].Id,true,false)).Visible)throw new Exception("mixed");
            }
            GD.Print("WARDROBE_OK");GetTree().Quit();
        }catch(Exception e){GD.PrintErr(e);GetTree().Quit(1);}
    }
}
