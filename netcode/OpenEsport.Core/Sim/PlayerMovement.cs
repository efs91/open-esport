using System;

namespace OpenEsport.Core
{
	/// <summary>
	/// Déplacement d'un joueur pour un tick. C'est LE code de référence : le serveur l'exécute pour décider,
	/// le client l'exécute pour prédire. Il ne dépend d'aucun moteur.
	/// </summary>
	public static class PlayerMovement
	{
		/// <param name="speedScale">Multiplicateur de vitesse de déplacement (à mains nues : <see cref="GameBalance.UnarmedMultiplier"/>). Le serveur et le client le calculent de la même façon, d'après l'arme en main.</param>
		public static void Step(ref PlayerState s, in PlayerInput inp, IWorld world, float dt, float speedScale = 1f)
		{
			bool jumpHeld = inp.Has(Buttons.Jump);
			bool jumpPressed = jumpHeld && (s.PrevButtons & (ushort)Buttons.Jump) == 0;
			bool interactPressed = inp.Has(Buttons.Interact) && (s.PrevButtons & (ushort)Buttons.Interact) == 0;
			bool crouchHeld = inp.Has(Buttons.Crouch);
			bool sprintHeld = inp.Has(Buttons.Sprint);
			s.PrevButtons = inp.ButtonBits;

			float sy = MathF.Sin(inp.Yaw), cy = MathF.Cos(inp.Yaw);

			if (s.Stance == Stance.Plane) { StepPlane(ref s, dt, jumpPressed, world); return; }
			if (s.Stance == Stance.Downed) { StepDowned(ref s, inp, world, dt, sy, cy); return; }
			if (s.Stance == Stance.Diving) { StepDive(ref s, inp, world, dt, inp.Forward > 12, sy, cy); return; }

			// ---- tyrolienne : en glissade sur un câble, ou saisie / tremplin
			if (s.ZipCooldown > 0f) s.ZipCooldown = MathF.Max(0f, s.ZipCooldown - dt);
			if (s.Zip != 0) { StepZip(ref s, world, dt, jumpPressed); return; }
			if (s.ClimbCooldown > 0f) s.ClimbCooldown = MathF.Max(0f, s.ClimbCooldown - dt);
			if (s.Stance == Stance.Climbing) { StepClimb(ref s, dt); return; }
			if (interactPressed && s.ZipCooldown <= 0f && TryGrab(ref s, world, -1, sy, cy)) return;
			if (s.AssistLine != 0)
			{
				if (s.OnGround) s.AssistLine = 0;
				else if (TryGrab(ref s, world, s.AssistLine - 1, sy, cy)) return;
			}
			if (s.OnGround && world.PadAt(s.Pos, out var pad)) Launch(ref s, pad, world);

			float f = inp.Forward / 127f, r = inp.Strafe / 127f;
			// escalade : en l'air, saut maintenu et face à un rebord à portée de main
			if (!s.OnGround && jumpHeld && f > 0.3f && s.ClimbCooldown <= 0f && TryClimb(ref s, world, sy, cy)) return;
			Vec3 wish = new Vec3(-sy, 0, -cy) * f + new Vec3(cy, 0, -sy) * r;
			float wl = wish.Length();
			if (wl > 1f) wish = wish / wl;

			Vec3 velH = new Vec3(s.Vel.X, 0, s.Vel.Z);
			if (s.SlideCooldown > 0f) s.SlideCooldown = MathF.Max(0f, s.SlideCooldown - dt);

			bool sliding = s.Stance == Stance.Sliding;
			bool slideJustStarted = false;

			if (sliding)
			{
				if (!(sprintHeld && crouchHeld) || !s.OnGround || jumpPressed)
				{
					EndSlide(ref s, crouchHeld);
					sliding = false;
				}
			}
			else if (sprintHeld && crouchHeld && s.OnGround && s.SlideCooldown <= 0f)
			{
				Vec3 dir = wl > 0.1f ? wish.Normalized() : velH.Normalized();
				float speed = velH.Length();
				if (dir.LengthSquared() > 0f && speed > 1f)
				{
					s.Stance = Stance.Sliding;
					// La moitié du gain multiplicatif est immédiate ; le reste arrive pendant la poussée.
					// Même calcul dans la prédiction et sur le serveur, sans nouveau message réseau.
					s.SlideSpeed = MathF.Min(speed * MathF.Sqrt(Sim.SlideStartMultiplier), Sim.SlideMaxSpeed);
					s.SlideTime = 0f;
					s.SlideDirX = dir.X;
					s.SlideDirZ = dir.Z;
					sliding = true;
					slideJustStarted = true;
				}
			}

			if (sliding)
			{
				var dir = new Vec3(s.SlideDirX, 0, s.SlideDirZ);
				if (!slideJustStarted)
				{
					float boostDt = Math.Clamp(Sim.SlideBoostTime - s.SlideTime, 0f, dt);
					s.SlideTime += dt;
					bool boosting = boostDt > 0f;
					// Un plancher/toit est horizontal, même si le terrain sous le bâtiment est en pente.
					bool onBox = world.GroundAt(s.Pos.X, s.Pos.Z, s.Pos.Y, Sim.StepHeight) > world.HeightAt(s.Pos.X, s.Pos.Z) + 0.01f;
					Vec3 n = onBox ? Vec3.Up : world.NormalAt(s.Pos.X, s.Pos.Z);
					float slope = MathF.Acos(Math.Clamp(n.Y, -1f, 1f));
					float accel = 0f;
					bool flat = slope < 0.05f;
					if (!flat)
					{
						Vec3 uphill = (Vec3.Up - n * n.Y).Normalized();
						float factor = slope / (MathF.PI / 2f);
						accel = uphill.Dot(dir) < 0f ? Sim.SlideDownAcceleration * factor : -Sim.SlideUpDeceleration * factor;
					}
					else if (!boosting) accel = -Sim.SlideFlatDeceleration;
					if (boosting)
					{
						// Complète le gain initial : au total x SlideStartMultiplier sur le plat.
						s.SlideSpeed *= MathF.Pow(Sim.SlideStartMultiplier, 0.5f * boostDt / Sim.SlideBoostTime);
						s.SlideSpeed += accel * dt;
						if (flat) s.SlideSpeed -= Sim.SlideFlatDeceleration * (dt - boostDt);
					}
					else s.SlideSpeed += accel * dt;
					s.SlideSpeed = Math.Clamp(s.SlideSpeed, 0f, Sim.SlideMaxSpeed);
				}
				velH = dir * s.SlideSpeed;
				if (s.SlideSpeed <= Sim.BaseSpeed)
				{
					EndSlide(ref s, crouchHeld);
					sliding = false;
					velH = Vec3.Zero;
				}
			}

			if (!sliding && s.AssistLine != 0 && !s.OnGround)
			{
				s.Stance = Stance.Standing;   // projeté par un tremplin : la trajectoire ne se corrige pas en l'air
			}
			else if (!sliding)
			{
				s.Stance = crouchHeld && s.OnGround ? Stance.Crouching : Stance.Standing;
				bool sprinting = sprintHeld && s.Stance != Stance.Crouching && f >= 0.5f;
				float mod = s.Stance == Stance.Crouching ? Sim.CrouchMultiplier : (sprinting ? Sim.SprintMultiplier : 1f);
				float speed = Sim.BaseSpeed * mod * speedScale;
				float accel = wish.Dot(velH) > 0f ? Sim.Acceleration : Sim.Deceleration;
				velH = Vec3.Lerp(velH, wish * speed, MathF.Min(1f, accel * dt));
			}

			float vy = s.Vel.Y;
			if (jumpPressed && s.OnGround)
			{
				vy = Sim.JumpSpeed;
				s.OnGround = false;
			}
			else if (!s.OnGround)
			{
				vy -= Sim.Gravity * dt;
			}
			else
			{
				vy = 0f;
			}

			Integrate(ref s, velH, vy, world, dt);
		}

		// ------------------------------------------------------------------ arrivée : avion, chute libre, parachute

		/// <summary>Un vol : de <paramref name="from"/> à <paramref name="to"/> (même altitude), avec l'instant où l'avion entre sur la carte et celui où il en sort.</summary>
		public static PlayerState BeginFlight(Vec3 from, Vec3 to, float enter, float exit)
			=> new PlayerState { Pos = from, Stance = Stance.Plane, OnGround = false, PlaneFrom = from, PlaneTo = to, PlaneEnter = enter, PlaneExit = exit, FlightT = 0f };

		public static Vec3 PlaneDirection(in PlayerState s)
		{
			float dx = s.PlaneTo.X - s.PlaneFrom.X, dz = s.PlaneTo.Z - s.PlaneFrom.Z;
			float l = MathF.Sqrt(dx * dx + dz * dz);
			return l < 1e-4f ? new Vec3(0, 0, -1) : new Vec3(dx / l, 0, dz / l);
		}

		/// <summary>On saute quand l'avion est au-dessus de la carte ; à la sortie de la carte, tout le monde est éjecté.</summary>
		private static void StepPlane(ref PlayerState s, float dt, bool jumpPressed, IWorld world)
		{
			s.FlightT += dt;
			var dir = PlaneDirection(s);
			float along = MathF.Max(0f, s.FlightT) * Sim.PlaneSpeed;
			s.Pos = new Vec3(s.PlaneFrom.X + dir.X * along, s.PlaneFrom.Y, s.PlaneFrom.Z + dir.Z * along);
			s.Vel = new Vec3(dir.X * Sim.PlaneSpeed, 0f, dir.Z * Sim.PlaneSpeed);
			bool window = s.FlightT >= s.PlaneEnter;
			if ((jumpPressed && window) || s.FlightT >= s.PlaneExit)
			{
				s.Stance = Stance.Diving;
				s.Vel = new Vec3(dir.X * Sim.PlaneSpeed * 0.6f, -6f, dir.Z * Sim.PlaneSpeed * 0.6f);
			}
		}

		/// <summary>
		/// Part (0 à 1) d'un piqué : 0 = redressé, à plat ventre, descente lente ; 1 = tête la première. On plonge en maintenant « avancer » (au moins 72 %,
		/// plus si on regarde vers le bas) ; en relâchant « avancer » on se redresse et on ralentit.
		/// </summary>
		public static float DiveAmount(float pitch, bool forwardHeld)
			=> forwardHeld ? MathF.Max(Math.Clamp(-pitch / 1.4f, 0f, 1f), 0.72f) : 0f;

		/// <summary>Inclinaison montrée sur le corps (0 à 1) d'après le « pitch » que le serveur réplique pour un plongeur : voir <see cref="DivePitch"/>.</summary>
		public static float DivePose(float pitch) => Math.Clamp(-pitch / 1.4f, 0f, 1f);

		/// <summary>Le « pitch » répliqué d'un plongeur : l'inclinaison de son piqué, pour que tout le monde voie la même pose.</summary>
		public static float DivePitch(float dive) => -dive * 1.4f;

		/// <summary>Part de piqué d'après la vitesse de chute (pour montrer la pose sans connaître les commandes).</summary>
		public static float DiveFromFall(float verticalSpeed) => Math.Clamp((-verticalSpeed - Sim.DiveFallMin) / (Sim.DiveFallMax * Sim.DiveSprintBoost - Sim.DiveFallMin), 0f, 1f);

		/// <summary>Chute libre dirigée comme dans Apex : le regard donne la direction, plus on plonge plus on tombe vite (et moins on avance) ;
		/// « avancer » = piqué, relâché = on se redresse et on ralentit. Pas de parachute : on se pose à la vitesse de la chute (freinée juste avant le sol).</summary>
		private static void StepDive(ref PlayerState s, in PlayerInput inp, IWorld world, float dt, bool plunge, float sy, float cy)
		{
			float f = inp.Forward / 127f, r = inp.Strafe / 127f;
			float terrain = world.HeightAt(s.Pos.X, s.Pos.Z);
			bool flare = s.Pos.Y - terrain < Sim.FlareHeight;
			float t = DiveAmount(inp.Pitch, plunge);
			float fall, glide;
			fall = Sim.DiveFallMin + (Sim.DiveFallMax - Sim.DiveFallMin) * t;
			glide = Sim.DiveGlideMax + (Sim.DiveGlideMin - Sim.DiveGlideMax) * t;
			if (plunge) fall *= Sim.DiveSprintBoost;
			if (flare) fall = MathF.Min(fall, Sim.FlareFall);
						var fwd = new Vec3(-sy, 0, -cy);
			var right = new Vec3(cy, 0, -sy);
			Vec3 wish = fwd + right * (r * 0.7f);
			var want = wish.LengthSquared() > 1e-6f ? wish.Normalized() * glide : Vec3.Zero;
			Vec3 velH = Vec3.Lerp(new Vec3(s.Vel.X, 0, s.Vel.Z), want, MathF.Min(1f, Sim.DiveAccelH * dt));
			float vy = s.Vel.Y + ((-fall) - s.Vel.Y) * MathF.Min(1f, Sim.DiveAccelV * dt);

			Vec3 old = s.Pos;
			Vec3 np = new Vec3(old.X + velH.X * dt, old.Y + vy * dt, old.Z + velH.Z * dt);
			np = world.ResolveObstacles(np, Sim.Radius, Sim.HeightStanding, 0.05f);
			world.ClampToBounds(ref np);
			float ground = world.GroundAt(np.X, np.Z, old.Y, 0.2f);
			if (np.Y <= ground)
			{
				s.Pos = new Vec3(np.X, ground, np.Z);
				s.Vel = Vec3.Zero;
				s.Stance = Stance.Standing;
				s.OnGround = true;
				return;
			}
			s.Pos = np;
			s.Vel = new Vec3(velH.X, vy, velH.Z);
		}

		/// <summary>À terre : on rampe lentement (pas de saut, de glissade ni d'escalade) en attendant d'être relevé.</summary>
		private static void StepDowned(ref PlayerState s, in PlayerInput inp, IWorld world, float dt, float sy, float cy)
		{
			float f = inp.Forward / 127f, r = inp.Strafe / 127f;
			Vec3 wish = new Vec3(-sy, 0, -cy) * f + new Vec3(cy, 0, -sy) * r;
			if (wish.Length() > 1f) wish = wish.Normalized();
			Vec3 velH = Vec3.Lerp(new Vec3(s.Vel.X, 0, s.Vel.Z), wish * Sim.DownedSpeed, MathF.Min(1f, Sim.Acceleration * dt));
			s.Stance = Stance.Downed;
			float vy = s.OnGround ? 0f : s.Vel.Y - Sim.Gravity * dt;
			Integrate(ref s, velH, vy, world, dt);
		}

		/// <summary>Cherche un rebord devant le joueur : un dessus (boîte, toit, relief) entre ClimbMinRise et ClimbMaxRise au-dessus des pieds, juste après un mur.</summary>
		public static bool FindLedge(Vec3 pos, IWorld world, float sy, float cy, out Vec3 landing)
		{
			landing = default;
			float fx = -sy, fz = -cy;
			float low = pos.Y + Sim.ClimbMinRise, high = pos.Y + Sim.ClimbMaxRise;
			for (int i = 0; i < 3; i++)
			{
				float d = 0.5f + 0.25f * i;
				float x = pos.X + fx * d, z = pos.Z + fz * d;
				float top = world.GroundAt(x, z, pos.Y, Sim.ClimbMaxRise);
				if (top < low || top > high) continue;
				// on se pose un peu plus loin sur le dessus ; si le rebord est étroit, au bord
				float lx = pos.X + fx * (d + 0.35f), lz = pos.Z + fz * (d + 0.35f);
				float ltop = world.GroundAt(lx, lz, pos.Y, Sim.ClimbMaxRise);
				if (ltop < low || ltop > high || MathF.Abs(ltop - top) > 0.25f) { lx = x; lz = z; ltop = top; }
				var target = new Vec3(lx, ltop, lz);
				var fixedPos = world.ResolveObstacles(target, Sim.Radius, Sim.HeightStanding, Sim.StepHeight);
				float mx = fixedPos.X - target.X, mz = fixedPos.Z - target.Z;
				if (mx * mx + mz * mz > 0.3f * 0.3f) continue;     // pas la place de se tenir debout
				landing = new Vec3(fixedPos.X, ltop, fixedPos.Z);
				return true;
			}
			return false;
		}

		private static bool TryClimb(ref PlayerState s, IWorld world, float sy, float cy)
		{
			if (!FindLedge(s.Pos, world, sy, cy, out var landing)) return false;
			s.Stance = Stance.Climbing;
			s.ClimbT = 0f;
			s.ClimbFrom = s.Pos;
			s.ClimbTo = landing;
			s.OnGround = false;
			s.Vel = Vec3.Zero;
			s.SlideSpeed = 0f;
			return true;
		}

		/// <summary>Hissage : on monte le long du mur, puis on passe sur le rebord.</summary>
		private static void StepClimb(ref PlayerState s, float dt)
		{
			s.ClimbT += dt;
			float u = Math.Clamp(s.ClimbT / Sim.ClimbTime, 0f, 1f);
			Vec3 a = s.ClimbFrom, b = s.ClimbTo;
			Vec3 prev = s.Pos, np;
			if (u < Sim.ClimbRisePart)
			{
				float k = u / Sim.ClimbRisePart;
				k = k * k * (3f - 2f * k);
				np = new Vec3(a.X, a.Y + (b.Y - a.Y) * k, a.Z);
			}
			else
			{
				float k = (u - Sim.ClimbRisePart) / (1f - Sim.ClimbRisePart);
				k = k * k * (3f - 2f * k);
				np = new Vec3(a.X + (b.X - a.X) * k, b.Y, a.Z + (b.Z - a.Z) * k);
			}
			s.Pos = np;
			s.Vel = new Vec3((np.X - prev.X) / dt, (np.Y - prev.Y) / dt, (np.Z - prev.Z) / dt);
			if (u >= 1f)
			{
				s.Pos = b;
				s.Vel = Vec3.Zero;
				s.Stance = Stance.Standing;
				s.OnGround = true;
				s.ClimbCooldown = 0.25f;
			}
		}

		/// <summary>Saisit un câble à portée de main. Le sens est celui dans lequel regarde le joueur.</summary>
		private static bool TryGrab(ref PlayerState s, IWorld world, int onlyLine, float sy, float cy)
		{
			var hand = new Vec3(s.Pos.X, s.Pos.Y + Sim.ZipHoldHeight, s.Pos.Z);
			if (!world.FindZipline(hand, Sim.ZipGrabHorizontal, Sim.ZipGrabVertical, onlyLine, out int line, out float dist)) return false;
			world.ZipPointAt(line, dist, out var p, out var tangent, out _);
			float facing = tangent.X * -sy + tangent.Z * -cy;     // composante du regard le long du câble
			s.Zip = (ushort)(line + 1);
			s.ZipDist = dist;
			s.ZipDir = (sbyte)(facing >= 0f ? 1 : -1);
			s.ZipSpeed = MathF.Max(Sim.ZipStartSpeed, MathF.Min(Sim.ZipSpeed, new Vec3(s.Vel.X, 0, s.Vel.Z).Length()));
			s.AssistLine = 0;
			s.Stance = Stance.Zipping;
			s.OnGround = false;
			s.Pos = new Vec3(p.X, p.Y - Sim.ZipHoldHeight, p.Z);
			s.Vel = tangent * (s.ZipSpeed * s.ZipDir);
			return true;
		}

		private static void StepZip(ref PlayerState s, IWorld world, float dt, bool jumpPressed)
		{
			if (!world.ZipPointAt(s.Zip - 1, s.ZipDist, out var p, out var tangent, out float length)) { LeaveZip(ref s, Vec3.Zero, 0f); return; }
			if (jumpPressed)
			{
				// on saute du câble, un peu vers le haut : de quoi se retourner et reprendre un câble dans l'autre sens
				LeaveZip(ref s, tangent * (s.ZipDir * s.ZipSpeed * 0.25f), 5.2f);
				return;
			}
			s.ZipSpeed = MathF.Min(Sim.ZipSpeed, s.ZipSpeed + Sim.ZipAcceleration * dt);
			s.ZipDist += s.ZipDir * s.ZipSpeed * dt;
			if (s.ZipDist <= 0f || s.ZipDist >= length)
			{
				s.ZipDist = Math.Clamp(s.ZipDist, 0f, length);
				world.ZipPointAt(s.Zip - 1, s.ZipDist, out p, out tangent, out _);
				s.Pos = new Vec3(p.X, p.Y - Sim.ZipHoldHeight, p.Z);
				LeaveZip(ref s, tangent * (s.ZipDir * s.ZipSpeed * 0.6f), 1.5f);   // bout du câble : on est lâché en avant
				return;
			}
			world.ZipPointAt(s.Zip - 1, s.ZipDist, out p, out tangent, out _);
			s.Pos = new Vec3(p.X, p.Y - Sim.ZipHoldHeight, p.Z);
			s.Vel = tangent * (s.ZipDir * s.ZipSpeed);
		}

		private static void LeaveZip(ref PlayerState s, Vec3 horizontal, float up)
		{
			s.Zip = 0;
			s.Stance = Stance.Standing;
			s.OnGround = false;
			s.ZipCooldown = 0.3f;
			s.Vel = new Vec3(horizontal.X, up + horizontal.Y, horizontal.Z);
		}

		/// <summary>Tremplin : projette le joueur jusqu'à la main-hauteur du câble visé, sans toucher au sol entre-temps.</summary>
		private static void Launch(ref PlayerState s, LaunchPad pad, IWorld world)
		{
			if (!world.ZipPointAt(pad.Line, pad.Dist, out var target, out _, out _)) return;
			float T = Sim.ZipLaunchTime;
			float dx = target.X - s.Pos.X, dz = target.Z - s.Pos.Z;
			float dy = (target.Y - Sim.ZipHoldHeight) - s.Pos.Y;
			s.Vel = new Vec3(dx / T, (dy + 0.5f * Sim.Gravity * T * T) / T, dz / T);
			s.OnGround = false;
			s.AssistLine = (ushort)(pad.Line + 1);
			s.Stance = Stance.Standing;
		}

		private static void EndSlide(ref PlayerState s, bool crouchHeld)
		{
			s.Stance = crouchHeld ? Stance.Crouching : Stance.Standing;
			s.SlideCooldown = Sim.SlideCooldown;
			s.SlideSpeed = 0f;
		}

		private static void Integrate(ref PlayerState s, Vec3 velH, float vy, IWorld world, float dt)
		{
			Vec3 old = s.Pos;
			float height = Sim.HeightFor(s.Stance);
			Vec3 np = new Vec3(old.X + velH.X * dt, old.Y, old.Z + velH.Z * dt);
			np = world.ResolveObstacles(np, Sim.Radius, height, Sim.StepHeight);
			world.ClampToBounds(ref np);
			float ground = world.GroundAt(np.X, np.Z, old.Y, Sim.StepHeight);
			bool onBox = ground > world.HeightAt(np.X, np.Z) + 0.01f;     // sur un sol, une marche ou un toit : pas de limite de pente

			if (s.OnGround)
			{
				float dh = ground - old.Y;
				float dx = np.X - old.X, dz = np.Z - old.Z;
				float hd = MathF.Sqrt(dx * dx + dz * dz);
				if (!onBox && hd > 1e-5f && dh / hd > Sim.MaxSlopeTan)
				{
					// pente trop raide : on reste en place
					np.X = old.X;
					np.Z = old.Z;
					ground = world.GroundAt(old.X, old.Z, old.Y, Sim.StepHeight);
					velH = Vec3.Zero;
				}
				if (ground >= old.Y - Sim.SnapDistance)
					np.Y = ground;
				else
				{
					// bord d'un à-pic : on tombe
					s.OnGround = false;
					np.Y = old.Y;
				}
				vy = 0f;
			}
			else
			{
				np.Y = old.Y + vy * dt;
				if (np.Y <= ground && vy <= 0f)
				{
					np.Y = ground;
					vy = 0f;
					s.OnGround = true;
				}
			}

			s.Pos = np;
			s.Vel = new Vec3(velH.X, vy, velH.Z);
		}

		/// <summary>Place un joueur au sol à une position donnée.</summary>
		public static PlayerState Spawn(Vec3 pos, IWorld world)
		{
			var s = new PlayerState { Pos = new Vec3(pos.X, world.GroundAt(pos.X, pos.Z, pos.Y, Sim.StepHeight), pos.Z), OnGround = true };
			return s;
		}
	}
}
