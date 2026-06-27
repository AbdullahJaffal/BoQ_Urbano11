;;; urb-area-compare.lsp
;;; يمسح جميع الشبكات تلقائياً، يجد كل البوريات ذات محطات تقاطع
;;; يقارن كل زوج ممكن — لجميع نقاط التقاطع من الأول حتى الأخير
;;; الخرج: ملف CSV قابل للفتح في Excel مباشرة

(vl-load-com)

(defun _all (code lst / r)
  (setq r '())
  (foreach p lst (if (= (car p) code) (setq r (append r (list (cdr p))))))
  r
)

;;; يقرأ NetArea المخزن (أول double في EXCAVATION_KAZI/SCENARIO_xxx)
(defun _sc-area (sta-id sc-key / eid sc dbls ints)
  (setq eid (cdr (assoc -1 (dictsearch sta-id "EXCAVATION_KAZI"))))
  (if (not eid) nil
    (progn
      (setq sc (dictsearch eid sc-key))
      (if (not sc) nil
        (progn
          (setq dbls (_all 40 sc) ints (_all 90 sc))
          (cond
            ((and ints (= (nth 0 ints) 0))        0.0)
            ((and dbls (> (length dbls) 0)) (nth 0 dbls))
            (t nil)
          )
        )
      )
    )
  )
)

;;; يُحضر محطات بورو → قائمة (dist sta-id is-bnd sta-name) مرتبة
(defun _load-pipe (pipe-id / res k eid si dist ints70 is-bnd)
  (setq res '())
  (vlax-for e (vlax-ename->vla-object pipe-id)
    (setq k (vla-get-name e))
    (if (= (substr k 1 4) "STA_")
      (progn
        (setq eid   (vlax-vla-object->ename e)
              si    (dictsearch eid "STATION_INFO")
              dist  (if si (cdr (assoc 40 si)) -1.0)
              ints70 (if si (_all 70 si) nil)
              is-bnd (and ints70 (= (nth 1 ints70) 1)))
        ;; (dist  sta-id  is-bnd  sta-name)
        (setq res (append res (list (list dist eid is-bnd k))))
      )
    )
  )
  (vl-sort res (function (lambda (a b) (< (car a) (car b)))))
)

(defun _sta-dist (s) (car   s))
(defun _sta-id   (s) (cadr  s))
(defun _sta-bnd  (s) (caddr s))
(defun _sta-name (s) (nth 3 s))

;;; منطقة التقاطع: من أول IsCrossingBoundary إلى آخره
(defun _zone (stas / i fi li zone)
  (setq i 0 fi -1 li -1)
  (foreach s stas
    (if (_sta-bnd s) (progn (if (= fi -1) (setq fi i)) (setq li i)))
    (setq i (1+ i))
  )
  (if (or (= fi -1) (= li fi)) nil
    (progn
      (setq zone '() i 0)
      (foreach s stas
        (if (and (>= i fi) (<= i li)) (setq zone (append zone (list s))))
        (setq i (1+ i))
      )
      zone
    )
  )
)

;;; فاصل الـ CSV — نقطة فاصلة للتوافق مع Excel العربي/التركي
(setq SEP ";")

(defun _n (x) (rtos x 2 6))   ; رقم عشري
(defun _n3 (x) (rtos x 2 3))  ; رقم 3 خانات

;;; سطر CSV: يُحوّل قائمة عناصر إلى سطر مفصول بـ SEP
(defun _row (items / s)
  (setq s "")
  (foreach it items
    (if (= s "") (setq s it) (setq s (strcat s SEP it)))
  )
  s
)

;;; يطبع مقارنة زوج واحد بصيغة CSV
(defun _compare-pair (f la zone-a lb zone-b / na nb n i s-a s-b
                        a50 aUp aLo b50 bUp bLo
                        s50 sUp sLo d-ul
                        any-diff T50a TUpa TLoa T50b TUpb TLob T50 TUp TLo)

  (setq na (length zone-a) nb (length zone-b) n (min na nb))

  ;; عنوان القسم
  (write-line (_row (list (strcat "=== " la "  vs  " lb " ===") "" "" "" "" "" "" "" "" "" "" "" "" "" "")) f)
  (write-line (_row (list (strcat "A: " (itoa na) " ist")
                          (strcat "[" (_n3 (_sta-dist (car zone-a))) " .. "
                                       (_n3 (_sta-dist (last zone-a))) " m]")
                          "" "" "" "" "" "" "" "" "" "" "" "" "")) f)
  (write-line (_row (list (strcat "B: " (itoa nb) " ist")
                          (strcat "[" (_n3 (_sta-dist (car zone-b))) " .. "
                                       (_n3 (_sta-dist (last zone-b))) " m]")
                          "" "" "" "" "" "" "" "" "" "" "" "" "")) f)
  (write-line "" f)

  ;; ── جدول A ─────────────────────────────────────────────────────────
  (write-line (_row (list (strcat "BORU A: " la) "" "" "" "" "")) f)
  (write-line (_row (list "Istasyon" "dist(m)" "50_50" "Upper" "Lower" "Upper-Lower")) f)
  (setq T50a 0.0 TUpa 0.0 TLoa 0.0)
  (foreach s zone-a
    (setq a50 (_sc-area (_sta-id s) "SCENARIO_50_50")
          aUp (_sc-area (_sta-id s) "SCENARIO_UPPER")
          aLo (_sc-area (_sta-id s) "SCENARIO_LOWER"))
    (if (and a50 aUp aLo)
      (progn
        (setq T50a (+ T50a a50) TUpa (+ TUpa aUp) TLoa (+ TLoa aLo))
        (write-line (_row (list (_sta-name s) (_n3 (_sta-dist s))
                                (_n a50) (_n aUp) (_n aLo)
                                (_n (- aUp aLo)))) f)
      )
      (write-line (_row (list (_sta-name s) (_n3 (_sta-dist s)) "VERI YOK" "" "" "")) f)
    )
  )
  (write-line (_row (list "TOPLAM" "" (_n T50a) (_n TUpa) (_n TLoa) (_n (- TUpa TLoa)))) f)
  (write-line "" f)

  ;; ── جدول B ─────────────────────────────────────────────────────────
  (write-line (_row (list (strcat "BORU B: " lb) "" "" "" "" "")) f)
  (write-line (_row (list "Istasyon" "dist(m)" "50_50" "Upper" "Lower" "Upper-Lower")) f)
  (setq T50b 0.0 TUpb 0.0 TLob 0.0)
  (foreach s zone-b
    (setq b50 (_sc-area (_sta-id s) "SCENARIO_50_50")
          bUp (_sc-area (_sta-id s) "SCENARIO_UPPER")
          bLo (_sc-area (_sta-id s) "SCENARIO_LOWER"))
    (if (and b50 bUp bLo)
      (progn
        (setq T50b (+ T50b b50) TUpb (+ TUpb bUp) TLob (+ TLob bLo))
        (write-line (_row (list (_sta-name s) (_n3 (_sta-dist s))
                                (_n b50) (_n bUp) (_n bLo)
                                (_n (- bUp bLo)))) f)
      )
      (write-line (_row (list (_sta-name s) (_n3 (_sta-dist s)) "VERI YOK" "" "" "")) f)
    )
  )
  (write-line (_row (list "TOPLAM" "" (_n T50b) (_n TUpb) (_n TLob) (_n (- TUpb TLob)))) f)
  (write-line "" f)

  ;; ── A+B جدول مدمج ─────────────────────────────────────────────────
  (write-line (_row (list "A+B (index eslemesi)" "" "" "" "" "" "" "" "" "" "" "" "" "" "")) f)
  (write-line (_row (list "#"
                          "Ist-A" "dist-A"
                          "Ist-B" "dist-B"
                          "A_50_50" "B_50_50" "Sum_50_50"
                          "A_Upper" "B_Upper" "Sum_Upper"
                          "A_Lower" "B_Lower" "Sum_Lower"
                          "Upper-Lower")) f)
  (setq i 0 any-diff nil T50 0.0 TUp 0.0 TLo 0.0)
  (while (< i n)
    (setq s-a (nth i zone-a) s-b (nth i zone-b))
    (setq a50 (_sc-area (_sta-id s-a) "SCENARIO_50_50")
          aUp (_sc-area (_sta-id s-a) "SCENARIO_UPPER")
          aLo (_sc-area (_sta-id s-a) "SCENARIO_LOWER")
          b50 (_sc-area (_sta-id s-b) "SCENARIO_50_50")
          bUp (_sc-area (_sta-id s-b) "SCENARIO_UPPER")
          bLo (_sc-area (_sta-id s-b) "SCENARIO_LOWER"))
    (if (and a50 aUp aLo b50 bUp bLo)
      (progn
        (setq s50 (+ a50 b50) sUp (+ aUp bUp) sLo (+ aLo bLo)
              d-ul (- sUp sLo))
        (setq T50 (+ T50 s50) TUp (+ TUp sUp) TLo (+ TLo sLo))
        (if (> (abs d-ul) 1e-6) (setq any-diff T))
        (write-line (_row (list (itoa i)
                                (_sta-name s-a) (_n3 (_sta-dist s-a))
                                (_sta-name s-b) (_n3 (_sta-dist s-b))
                                (_n a50) (_n b50) (_n s50)
                                (_n aUp) (_n bUp) (_n sUp)
                                (_n aLo) (_n bLo) (_n sLo)
                                (_n d-ul))) f)
      )
    )
    (setq i (1+ i))
  )
  ;; صف المجاميع
  (write-line (_row (list "TOPLAM"
                          "" "" "" ""
                          (_n T50a) (_n T50b) (_n T50)
                          (_n TUpa) (_n TUpb) (_n TUp)
                          (_n TLoa) (_n TLob) (_n TLo)
                          (_n (- TUp TLo)))) f)
  (write-line "" f)
  ;; النتيجة
  (write-line (_row (list "SONUC"
                          (if any-diff
                            "FARK VAR => sorun ALAN hesabinda"
                            "FARK YOK => sorun HACIM entegrasyonunda")
                          "" "" "" "" "" "" "" "" "" "" "" "" "")) f)
  (write-line "" f)
  (write-line "" f)
)

;;; ─────────────────────────────────────────────────────────────────────────
(defun c:URB-COMPARE (/ boq-id nid all-zones
                        net-id net-name pipe-id pipe-key lbl stas z
                        f out-path ai bi za zb)

  (setq boq-id (cdr (assoc -1 (dictsearch (namedobjdict) "URBANO_BOQ"))))
  (if (not boq-id) (progn (prompt "\n[HATA] URBANO_BOQ yok.") (exit)))
  (setq nid (cdr (assoc -1 (dictsearch boq-id "AGLAR"))))
  (if (not nid) (setq nid boq-id))

  ;; ── جمع كل البوريات ذات منطقة تقاطع ───────────────────────────────
  (setq all-zones '())
  (vlax-for ne (vlax-ename->vla-object nid)
    (setq net-name (vla-get-name ne) net-id (vlax-vla-object->ename ne))
    (vlax-for pe (vlax-ename->vla-object net-id)
      (setq pipe-key (vla-get-name pe))
      (if (= (substr pipe-key 1 2) "P_")
        (progn
          (setq pipe-id (vlax-vla-object->ename pe)
                stas    (_load-pipe pipe-id)
                z       (_zone stas))
          (if z
            (setq all-zones
              (append all-zones
                (list (list (strcat net-name "/" pipe-key) z))))
          )
        )
      )
    )
  )

  ;; ── ملف الخرج ────────────────────────────────────────────────────────
  (setq out-path "C:\\Temp\\urb_compare.csv")
  (if (not (vl-file-directory-p "C:\\Temp")) (vl-mkdir "C:\\Temp"))
  (setq f (open out-path "w"))
  (if (not f) (progn (prompt "\n[HATA] Dosya acilamadi.") (exit)))

  (write-line (_row (list "URB-COMPARE: Tum kesisim cifti karsilastirmasi" "" "" "" "")) f)
  (write-line (_row (list "Kesisim bolgesi bulunan boru" (itoa (length all-zones)) "" "" "")) f)
  (foreach z all-zones
    (write-line (_row (list (car z)
                            (strcat "[" (_n3 (_sta-dist (car (cadr z)))) " .. "
                                        (_n3 (_sta-dist (last (cadr z)))) " m]")
                            "" "" "")) f)
  )
  (write-line "" f)

  (if (< (length all-zones) 2)
    (progn
      (write-line (_row (list "En az 2 kesisim bolgesi gerekli!" "" "" "" "")) f)
      (close f) (startapp "explorer" out-path) (exit)
    )
  )

  ;; ── كل الأزواج الممكنة (i,j) حيث i < j ─────────────────────────────
  (setq ai 0)
  (while (< ai (- (length all-zones) 1))
    (setq bi (1+ ai))
    (while (< bi (length all-zones))
      (setq za (nth ai all-zones) zb (nth bi all-zones))
      (_compare-pair f (car za) (cadr za) (car zb) (cadr zb))
      (setq bi (1+ bi))
    )
    (setq ai (1+ ai))
  )

  (write-line (_row (list "=== Bitti ===" "" "" "" "")) f)
  (close f)
  (prompt (strcat "\nKaydedildi: " out-path "\n"))
  (startapp "explorer" out-path)
  (princ)
)
