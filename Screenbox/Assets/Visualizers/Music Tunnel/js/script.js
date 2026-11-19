const container = document.getElementById("threejs-container");
const trackContainer = document.getElementById("track-container");
const albumart = document.getElementById("albumart");
const headerTitle = document.getElementById("track-title");
const headerArtist = document.getElementById("track-artist");

let currentTextureSrc = null;
let latestRequestedSrc = null;
let fallbackTimer = null;
let clock = new THREE.Clock();
const gui = new dat.GUI();
// is first texture in use for transition
let isTexture0 = false;
// transition animation queue
let textureQueue = Promise.resolve();
let isPaused = false,
  elapsedResetTime = 21600,
  elapsedPreviousTime = 0;

const colorThief = new ColorThief();
const backgroundSrcDefault = "media/background.jpg";
let scene, camera, renderer, material;
let isRenderLoopRunning = false;
let pendingTrackData = undefined;

let settings = {
  scale: 1,
  debug: false,
  fps: 60,
  parallaxVal: 0,
};

async function init() {
  renderer = new THREE.WebGLRenderer({
    antialias: false,
    preserveDrawingBuffer: false,
  });
  renderer.setSize(window.innerWidth, window.innerHeight);
  renderer.setPixelRatio(settings.scale);
  container.appendChild(renderer.domElement);
  scene = new THREE.Scene();
  camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);

  material = new THREE.ShaderMaterial({
    uniforms: {
      u_tex0: { type: "t" },
      u_tex1: { type: "t" },
      u_time: { value: 0, type: "f" },
      u_blend: { value: 0, type: "f" },
      u_speed: { value: 0.1, type: "f" },
      u_square: { value: true, type: "b" },
      u_resolution: {
        value: new THREE.Vector2(window.innerWidth * settings.scale, window.innerHeight * settings.scale),
        type: "v2",
      },
      u_center: { value: false, type: "b" },
      u_center_radius: { value: 1, type: "f" },
      u_center_color: { type: "c", value: { r: 0, g: 0, b: 0 } },
    },
    vertexShader: `
      varying vec2 vUv;
      void main() {
        vUv = uv;
        gl_Position = vec4( position, 1.0 );
      }
    `,
  });
  material.fragmentShader = await (await fetch("shaders/tunnel.frag")).text();

  const quad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2, 1, 1), material);
  scene.add(quad);

  if (settings.debug) {
    gui.show();
  } else {
    gui.hide();
  }

  // Replay any track update that arrived before shader initialization finished
  if (pendingTrackData !== undefined) {
    const queued = pendingTrackData;
    pendingTrackData = undefined;
    await livelyCurrentTrack(queued);
  }

  if (!isRenderLoopRunning) {
    isRenderLoopRunning = true;
    render();
  }
}

window.addEventListener("resize", function () {
  if (!renderer || !material) return;
  renderer.setSize(window.innerWidth, window.innerHeight);
  material.uniforms.u_resolution.value = new THREE.Vector2(
    window.innerWidth * settings.scale,
    window.innerHeight * settings.scale
  );
  renderer.render(scene, camera);
});

// parallax
document.addEventListener("mousemove", function (event) {
  if (settings.parallaxVal == 0) return;

  const x = (window.innerWidth - event.pageX * settings.parallaxVal) / 90;
  const y = (window.innerHeight - event.pageY * settings.parallaxVal) / 90;

  container.style.transform = `translateX(${x}px) translateY(${y}px) scale(1.09)`;
});

function setScale(value) {
  if (settings.scale == value) return;

  settings.scale = value;
  if (!renderer || !material) return;
  renderer.setPixelRatio(settings.scale);
  material.uniforms.u_resolution.value = new THREE.Vector2(
    window.innerWidth * settings.scale,
    window.innerHeight * settings.scale
  );
}

function render() {
  setTimeout(function () {
    requestAnimationFrame(render);
  }, 1000 / settings.fps);

  if (clock.getElapsedTime() > elapsedResetTime) clock = new THREE.Clock();
  material.uniforms.u_time.value = clock.getElapsedTime();

  renderer.render(scene, camera);
}

init();

function livelyWallpaperPlaybackChanged(data) {
  var obj = typeof data === "string" ? JSON.parse(data) : data;
  isPaused = obj.IsPaused;

  if (isPaused) {
    elapsedPreviousTime = clock.getElapsedTime();
    elapsedPreviousTime = elapsedPreviousTime > elapsedResetTime ? 0 : elapsedPreviousTime;
    clock.stop();
  } else {
    clock.start();
    clock.elapsedTime = elapsedPreviousTime;
  }
}

function livelyPropertyListener(name, val) {
  if (!material) return;
  switch (name) {
    case "speed":
      material.uniforms.u_speed.value = val;
      break;
    case "isSquare":
      material.uniforms.u_square.value = val;
      break;
    case "colorRadius":
      material.uniforms.u_center_radius.value = val;
      break;
    case "blurIntensity":
      container.style.filter = "blur(" + val + "px)";
      break;
    case "displayScaling":
      setScale(val);
      break;
    case "parallaxIntensity":
      settings.parallaxVal = val;
      break;
    case "fpsLock":
      settings.fps = val ? 30 : 60;
      break;
  }
}

async function livelyCurrentTrack(data) {
  if (!material) {
    pendingTrackData = data;
    return;
  }

  let obj = typeof data === "string" ? JSON.parse(data) : data;
  if (obj) {
    headerTitle.innerText = obj.Title || "";
    headerArtist.innerText = obj.Artist || "";

    if (obj.Thumbnail && obj.Thumbnail.length > 0) {
      if (fallbackTimer) {
        clearTimeout(fallbackTimer);
        fallbackTimer = null;
      }

      const base64String = !obj.Thumbnail.startsWith("data:image/")
        ? "data:image/png;base64," + obj.Thumbnail
        : obj.Thumbnail;

      albumart.src = base64String;
      setTexture(base64String);
    } else {
      // Wait briefly for async video thumbnail extraction before showing default purple fallback
      if (fallbackTimer) clearTimeout(fallbackTimer);
      fallbackTimer = setTimeout(() => {
        fallbackTimer = null;
        albumart.src = backgroundSrcDefault;
        setTexture(backgroundSrcDefault);
      }, 450);
    }

    material.uniforms.u_center.value = true;
    trackContainer.style.opacity = 1;
  } else {
    if (fallbackTimer) {
      clearTimeout(fallbackTimer);
      fallbackTimer = null;
    }
    albumart.src = backgroundSrcDefault;
    setTexture(backgroundSrcDefault);
    material.uniforms.u_center.value = false;
    trackContainer.style.opacity = 0;
  }
}

albumart.addEventListener("load", function () {
  try {
    let color = colorThief.getPalette(albumart, 6);
    color.unshift(colorThief.getColor(albumart));

    let mainColor = `rgb(${color[1].toString()})`;
    let minc = -999;
    for (let i = 1; i < color.length; i++) {
      let tmp = contrast(color[0], color[i]);
      if (tmp > minc) {
        minc = tmp;
        mainColor = `rgb(${color[i].toString()})`;
      }
    }

    setColor(mainColor, `rgb(${color[0].toString()})`);
  } catch (e) {
    // Ignore ColorThief errors on fallback/unloaded images
  }
});

albumart.addEventListener("error", function () {
  if (!albumart.src.endsWith(backgroundSrcDefault)) {
    albumart.src = backgroundSrcDefault;
  }
});

async function setTexture(src) {
  latestRequestedSrc = src;
  if (src === currentTextureSrc) return;

  const currentOperation = textureQueue.then(async () => {
    if (material == null || src !== latestRequestedSrc || src === currentTextureSrc) return;

    return new Promise((resolve) => {
      new THREE.TextureLoader().load(
        src,
        async function (tex) {
          if (src !== latestRequestedSrc) {
            tex.dispose();
            resolve();
            return;
          }

          currentTextureSrc = src;
          tex.wrapS = THREE.MirroredRepeatWrapping;
          tex.wrapT = THREE.MirroredRepeatWrapping;

          if (isTexture0) material.uniforms.u_tex0.value = tex;
          else material.uniforms.u_tex1.value = tex;

          await showTransition(isTexture0);
          isTexture0 = !isTexture0;

          resolve();
        },
        undefined,
        function () {
          // Always resolve on error so textureQueue never deadlocks
          resolve();
        }
      );
    });
  });

  textureQueue = currentOperation;
}

async function showTransition(isTexture0) {
  return new Promise((resolve) => {
    const initialValue = isTexture0 ? 1 : 0;
    const finalValue = isTexture0 ? 0 : 1;
    const duration = 500;
    let startTime;

    function animate(timestamp) {
      if (!startTime) startTime = timestamp;
      const progress = Math.min((timestamp - startTime) / duration, 1);
      const val = initialValue + (finalValue - initialValue) * progress;
      material.uniforms.u_blend.value = val;

      if (progress < 1) {
        requestAnimationFrame(animate);
      } else {
        if (isTexture0 && material.uniforms.u_tex1.value) {
          material.uniforms.u_tex1.value.dispose();
          material.uniforms.u_tex1.value = null;
        } else if (!isTexture0 && material.uniforms.u_tex0.value) {
          material.uniforms.u_tex0.value.dispose();
          material.uniforms.u_tex0.value = null;
        }
        resolve();
      }
    }
    requestAnimationFrame(animate);
  });
}

function setColor(mainColor, shadowColor) {
  document.documentElement.style.setProperty("--mainColor", mainColor);
  document.documentElement.style.setProperty("--shadowColor", shadowColor);
  if (material != null) material.uniforms.u_center_color.value = new THREE.Color(shadowColor);
}

function luminance(r, g, b) {
  var a = [r, g, b].map(function (v) {
    v /= 255;
    return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
  });
  return a[0] * 0.2126 + a[1] * 0.7152 + a[2] * 0.0722;
}

function contrast(rgb1, rgb2) {
  var lum1 = luminance(rgb1[0], rgb1[1], rgb1[2]);
  var lum2 = luminance(rgb2[0], rgb2[1], rgb2[2]);
  var brightest = Math.max(lum1, lum2);
  var darkest = Math.min(lum1, lum2);
  return (brightest + 0.05) / (darkest + 0.05);
}
