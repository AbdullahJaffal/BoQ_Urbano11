;;; UBQ_PEEK.lsp  –  يقرأ من NOD بيانات أول أنبوب في أول شبكة
;;; تحميل : (load "D:/Abdullah-ElsaProje/Software/UrbanoMetraj/UBQ_PEEK.lsp")
;;; تشغيل : UBQ_PEEK

(defun c:UBQ_PEEK (/
    nod entry root_id aglar_id
    net_result net_name net_ename
    pipe_result pipe_name pipe_ename
    sta_result sta_name sta_ename
    meta_fdata si_fdata pb_fdata
    diam_mm OD
    sTZ eTZ inv_s inv_e dep_s dep_e
    ter_z inv_z true_d
    pb_y0 pb_y1 pb_y2 pb_y3 pb_zmin pb_zmax)

  (vl-load-com)

  ;; ── helpers ──────────────────────────────────────────────────────────────

  ;; فلتر "whitelist": يُبقي فقط كودات البيانات الفعلية المستخدمة في الكود
  ;;   1  = DxfCode.Text  (Str)
  ;;   40 = DxfCode.Real  (Dbl)
  ;;   70 = DxfCode.Int16 (I16)
  ;;   90 = DxfCode.Int32 (I32)
  ;; أي كود آخر (100=header, 280=IsHardOwner, 330=owner, ...) يُحذف
  (defun _fget (ename)
    (vl-remove-if
      '(lambda (x) (not (member (car x) '(1 40 70 90))))
      (entget ename)))

  ;; القيمة رقم n من القائمة المفلترة
  (defun _rv (lst n / item)
    (setq item (nth n lst))
    (if item (cdr item) nil))

  ;; طباعة أول n عنصر مع كوداتها للتشخيص
  (defun _dump (lst n / i item)
    (setq i 0)
    (while (and (< i n) (setq item (nth i lst)))
      (prompt (strcat "\n    [" (itoa i) "] code=" (itoa (car item))
                      "  val=" (vl-princ-to-string (cdr item))))
      (setq i (1+ i))))

  ;; أول إدخال في القاموس لا اسمه في القائمة المستثناة
  (defun _first_entry (dict_id excluded / vla_d fname feid cname)
    (setq vla_d (vlax-ename->vla-object dict_id)
          fname nil  feid nil)
    (vlax-for item vla_d
      (if (null fname)
        (progn
          (setq cname (vlax-get item 'Name))
          (if (not (member cname excluded))
            (progn
              (setq fname cname)
              (setq feid  (vlax-vla-object->ename item)))))))
    (if fname (list fname feid) nil))

  ;; ── تنقل في شجرة NOD ─────────────────────────────────────────────────────

  (setq nod (namedobjdict))

  (setq entry (dictsearch nod "URBANO_BOQ"))
  (if (null entry)
    (progn (prompt "\n[!] URBANO_BOQ غير موجود. شغّل URBANO_BOQ أولاً.") (exit)))
  (setq root_id (cdr (assoc -1 entry)))

  (setq entry (dictsearch root_id "AGLAR"))
  (if (null entry)
    (progn (prompt "\n[!] AGLAR غير موجود.") (exit)))
  (setq aglar_id (cdr (assoc -1 entry)))

  (setq net_result (_first_entry aglar_id '()))
  (if (null net_result)
    (progn (prompt "\n[!] لا توجد شبكات.") (exit)))
  (setq net_name  (car net_result)
        net_ename (cadr net_result))

  (setq pipe_result (_first_entry net_ename '("NETWORK_META" "MANHOLE_STACKS")))
  (if (null pipe_result)
    (progn (prompt "\n[!] لا توجد أنابيب.") (exit)))
  (setq pipe_name  (car pipe_result)
        pipe_ename (cadr pipe_result))

  ;; ── METADATA ─────────────────────────────────────────────────────────────

  (setq entry (dictsearch pipe_ename "METADATA"))
  (if (null entry)
    (progn (prompt "\n[!] METADATA غير موجودة.") (exit)))
  (setq meta_fdata (_fget (cdr (assoc -1 entry))))

  ;; تشخيص: اطبع أول 8 عناصر لنتحقق من الإندكسات
  (prompt "\n  [DEBUG] أول 8 حقول في METADATA بعد الفلتر:")
  (_dump meta_fdata 8)
  (prompt "\n")

  ;; ترتيب حقول METADATA (بعد فلتر whitelist):
  ;;  0 (code 1)  StartNodeName
  ;;  1 (code 1)  EndNodeName
  ;;  2 (code 1)  PipeName
  ;;  3 (code 1)  NetName
  ;;  4 (code 90) DiameterMm
  ;;  5 (code 1)  Material
  ;;  6 (code 40) PipeOuterDiamM
  ;;  7 (code 40) Length2D
  ;;  8 (code 40) StartX
  ;;  9 (code 40) StartY
  ;; 10 (code 40) StartTerrainZ
  ;; 11 (code 40) EndX
  ;; 12 (code 40) EndY
  ;; 13 (code 40) EndTerrainZ
  ;; 14 (code 40) InvertStart     <-- = LL10 − OD
  ;; 15 (code 40) InvertEnd       <-- = LL11 − OD
  ;; 16 (code 40) DepthToInvStart
  ;; 17 (code 40) DepthToInvEnd

  (setq diam_mm (_rv meta_fdata  4)
        OD      (_rv meta_fdata  6)
        sTZ     (_rv meta_fdata 10)
        eTZ     (_rv meta_fdata 13)
        inv_s   (_rv meta_fdata 14)
        inv_e   (_rv meta_fdata 15)
        dep_s   (_rv meta_fdata 16)
        dep_e   (_rv meta_fdata 17))

  ;; ── STATION_INFO (أول محطة) ──────────────────────────────────────────────

  (setq sta_result (_first_entry pipe_ename '("METADATA")))
  (if sta_result
    (progn
      (setq sta_name  (car sta_result)
            sta_ename (cadr sta_result))

      (setq entry (dictsearch sta_ename "STATION_INFO"))
      (if entry
        (progn
          (setq si_fdata (_fget (cdr (assoc -1 entry))))
          ;; STATION_INFO (code 40 فقط في أول 13 حقل):
          ;;  0 dbl StationDist
          ;;  1 dbl WorldX
          ;;  2 dbl WorldY
          ;;  3 dbl TerrainZ
          ;;  4 dbl InvertZ
          ;;  5 dbl TrueDepth
          (setq ter_z  (_rv si_fdata 3)
                inv_z  (_rv si_fdata 4)
                true_d (_rv si_fdata 5))))

      ;; PIPE_BODY
      (setq entry (dictsearch sta_ename "PIPE_BODY"))
      (if entry
        (progn
          (setq pb_fdata (_fget (cdr (assoc -1 entry))))
          ;; idx 0 = area (code 40)
          ;; ثم WritePoly4: v0(u,z) v1(u,z) v2(u,z) v3(u,z) → كلها code 40
          ;; idx: 1=u0 2=z0  3=u1 4=z1  5=u2 6=z2  7=u3 8=z3
          (setq pb_y0 (_rv pb_fdata 2)
                pb_y1 (_rv pb_fdata 4)
                pb_y2 (_rv pb_fdata 6)
                pb_y3 (_rv pb_fdata 8))
          (if (and pb_y0 pb_y1 pb_y2 pb_y3)
            (progn
              (setq pb_zmin (min pb_y0 pb_y1 pb_y2 pb_y3))
              (setq pb_zmax (max pb_y0 pb_y1 pb_y2 pb_y3))))))))

  ;; ── طباعة النتائج ────────────────────────────────────────────────────────

  (prompt "\n===================================================")
  (prompt "\n  UBQ_PEEK  |  بيانات من NOD")
  (prompt "\n===================================================")
  (prompt (strcat "\n  الشبكة  : " net_name))
  (prompt (strcat "\n  البوري  : " pipe_name))

  (prompt "\n--- ابعاد البوري -----------------------------------")
  (if diam_mm
    (prompt (strcat "\n  DiameterMm    (قطر اسمي  PIPE_NO) = "
                    (itoa (fix diam_mm)) " مم"))
    (prompt "\n  DiameterMm = nil"))
  (if OD
    (progn
      (prompt (strcat "\n  PipeOuterDiam (قطر خارجي PIPE_DV) = "
                      (rtos (* OD 1000.0) 2 3) " مم  =  " (rtos OD 2 6) " م"))
      (prompt (strcat "\n  نصف القطر الخارجي                 = "
                      (rtos (* (/ OD 2.0) 1000.0) 2 3) " مم")))
    (prompt "\n  PipeOuterDiam = nil"))

  (prompt "\n--- مستويات الانبوب --------------------------------")
  (if sTZ
    (prompt (strcat "\n  StartTerrainZ  (منسوب ارض - بداية) = " (rtos sTZ  2 3) " م")))
  (if inv_s
    (prompt (strcat "\n  InvertStart    (تدفق      - بداية) = " (rtos inv_s 2 3) " م")))
  (if dep_s
    (prompt (strcat "\n  DepthToInvS    (عمق تدفق  - بداية) = " (rtos dep_s 2 3) " م")))
  (prompt "\n")
  (if eTZ
    (prompt (strcat "\n  EndTerrainZ    (منسوب ارض - نهاية) = " (rtos eTZ  2 3) " م")))
  (if inv_e
    (prompt (strcat "\n  InvertEnd      (تدفق      - نهاية) = " (rtos inv_e 2 3) " م")))
  (if dep_e
    (prompt (strcat "\n  DepthToInvE    (عمق تدفق  - نهاية) = " (rtos dep_e 2 3) " م")))

  (prompt "\n--- حسابات التحقق ----------------------------------")
  (if (and inv_s OD)
    (prompt (strcat "\n  InvertStart + OD = " (rtos (+ inv_s OD) 2 3)
                    " م   <-- يجب ان يساوي LL10 في Urbano")))
  (if (and inv_e OD)
    (prompt (strcat "\n  InvertEnd   + OD = " (rtos (+ inv_e OD) 2 3)
                    " م   <-- يجب ان يساوي LL11 في Urbano")))
  (if (and sTZ inv_s)
    (prompt (strcat "\n  Terrain - Invert (بداية) = " (rtos (- sTZ inv_s) 2 3) " م")))

  (if (and ter_z inv_z true_d)
    (progn
      (prompt (strcat "\n--- محطة: " sta_name " ---"))
      (prompt (strcat "\n  TerrainZ  = " (rtos ter_z  2 3) " م"))
      (prompt (strcat "\n  InvertZ   = " (rtos inv_z  2 3) " م"))
      (prompt (strcat "\n  TrueDepth = " (rtos true_d 2 3) " م"))
      (prompt (strcat "\n  TerrainZ - InvertZ = " (rtos (- ter_z inv_z) 2 3) " م"))
      (if OD
        (prompt (strcat "\n  InvertZ + OD = " (rtos (+ inv_z OD) 2 3) " م  <-- LL10 المحسوب")))))

  (if (and pb_zmin pb_zmax)
    (progn
      (prompt "\n--- مضلع جسم البوري (PipeBody) --------------------")
      (prompt (strcat "\n  Z_min = " (rtos pb_zmin 2 4) " م   <-- قاع البوري"))
      (prompt (strcat "\n  Z_max = " (rtos pb_zmax 2 4) " م   <-- قمة البوري"))
      (if OD
        (progn
          (prompt (strcat "\n  Z_max - Z_min = " (rtos (- pb_zmax pb_zmin) 2 4)
                          " م   (يجب ان يساوي OD = " (rtos OD 2 4) " م)"))
          (if inv_z
            (prompt (strcat "\n  Z_min - InvertZ = " (rtos (- pb_zmin inv_z) 2 4)
                            " م   (يجب ان يكون صفر تقريبا)")))))))

  (prompt "\n===================================================\n")
  (princ))
;;; EOF
